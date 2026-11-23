using System.Collections.Concurrent;
using DuckDB.NET.Data;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Store;
using SData.Settings;
using System.Diagnostics;

namespace SData.Store.Core;

/// <summary>
/// DuckDB 连接管理器（单进程持有数据库实例，写串行 + 读克隆连接）.
/// 内存/线程/并发等运行参数由 ITuningState 在初始化时注入（自动调控，不读用户配置）.
/// </summary>
public class DuckDbConnectionManager : IAsyncDisposable, IStoreInfo, IStorePerformanceMetrics
{
    private readonly DuckDbOptions _options;
    private readonly S3Options _s3;
    private readonly bool _traceIndexEnabled;
    private readonly ILogger<DuckDbConnectionManager> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SemaphoreSlim _queryLock;
    private readonly SemaphoreSlim _alertQueryLock = new(1, 1);
    private readonly SemaphoreSlim _maintenanceLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string[]> _logColumns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _views = new(StringComparer.OrdinalIgnoreCase);
    private DuckDBConnection? _master;
    private int _disposed;
    private long _lastResourceDiagnosticTicks;
    private sealed record MemoryState(string Configured, string Effective);
    private MemoryState _memory = new(string.Empty, string.Empty);

    /// <summary>配置的内存限制.</summary>
    public string MemoryLimit => Volatile.Read(ref _memory).Configured;

    /// <summary>DuckDB 读回的实际生效限制.</summary>
    public string EffectiveMemoryLimit => Volatile.Read(ref _memory).Effective;
    private readonly ArchiveCoordination _archiveCoordination = new();
    private readonly StorePerformanceMetrics _performanceMetrics = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tableLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _publishedSnapshots = new(StringComparer.Ordinal);

    public StorePerformanceSnapshot Snapshot => _performanceMetrics.Snapshot;

    public async Task<IDisposable> ReadSourcesAsync(CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        var lease = await _archiveCoordination.ReadAsync(token);
        _performanceMetrics.RecordSourceLockWait(started);
        return lease;
    }

    internal async Task<IDisposable> ChangeSourcesAsync(CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        var lease = await _archiveCoordination.WriteAsync(token);
        _performanceMetrics.RecordSourceLockWait(started);
        var acquired = Stopwatch.GetTimestamp();
        return new ArchiveCoordination.Lease(() =>
        {
            try
            {
                lease.Dispose();
            }
            finally
            {
                _performanceMetrics.RecordSourceSwitchLockHold(acquired);
            }
        });
    }

    internal async Task<IDisposable> LockTableAsync(string table, CancellationToken token)
    {
        var gate = _tableLocks.GetOrAdd(table, _ => new SemaphoreSlim(1, 1));
        var started = Stopwatch.GetTimestamp();
        await gate.WaitAsync(token);
        _performanceMetrics.RecordTableLockWait(started);
        return new ArchiveCoordination.Lease(() => gate.Release());
    }

    /// <summary>查询准入覆盖执行及结果读取；等待期间允许取消，不占用写入门.</summary>
    internal async Task<IDisposable> LockQueryAsync(CancellationToken token)
    {
        var gate = Volatile.Read(ref _queryLock);
        var started = Stopwatch.GetTimestamp();
        await gate.WaitAsync(token);
        _performanceMetrics.RecordQueryLockWait(started);
        return new ArchiveCoordination.Lease(() => gate.Release());
    }

    /// <summary>告警评估专用查询准入；不占普通用户查询槽.</summary>
    internal async Task<IDisposable> LockAlertQueryAsync(CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        await _alertQueryLock.WaitAsync(token);
        _performanceMetrics.RecordAlertQueryLockWait(started);
        return new ArchiveCoordination.Lease(() => _alertQueryLock.Release());
    }

    /// <summary>批量事务与 DDL/后台写共用写门；调用前完成建表，门内不得重入写接口.</summary>
    internal async Task<IDisposable> LockWriteAsync(CancellationToken token)
    {
        await AcquireWriteLockAsync(token);
        return new ArchiveCoordination.Lease(() => _writeLock.Release());
    }

    /// <summary>维护任务串行准入；COPY、聚合、回收与 CHECKPOINT 共用一个并发 1 的维护门.</summary>
    internal async Task<IDisposable> LockMaintenanceAsync(CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        await _maintenanceLock.WaitAsync(token);
        _performanceMetrics.RecordMaintenanceLockWait(started);
        return new ArchiveCoordination.Lease(() => _maintenanceLock.Release());
    }

    /// <summary>
    /// 在维护门持有期间使用克隆连接执行只读来源的导出、索引或检查点，不获取写门。
    /// 调用方负责持有维护门；SQL 不接收终端用户输入。
    /// </summary>
    internal async Task<int> ExecuteMaintenanceAsync(string sql, IReadOnlyCollection<DuckDBParameter>? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = CloneConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        ApplyParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal void RecordIngestAppend(long started) => _performanceMetrics.RecordIngestAppend(started);

    internal void RecordIngestCommit(long started) => _performanceMetrics.RecordIngestCommit(started);

    /// <inheritdoc/>
    public void RecordIngestAppendRowLoop(double seconds) => _performanceMetrics.RecordIngestAppendRowLoop(seconds);

    /// <inheritdoc/>
    public void RecordIngestAppendClose(double seconds) => _performanceMetrics.RecordIngestAppendClose(seconds);

    /// <inheritdoc/>
    public void RecordIngestAppendRows(long count) => _performanceMetrics.RecordIngestAppendRows(count);

    /// <inheritdoc/>
    public void RecordIngestFastRejected(long count) => _performanceMetrics.RecordIngestFastRejected(count);

    internal void RecordRollupDuration(long started) => _performanceMetrics.RecordRollupDuration(started);

    internal void RecordRollupRows(long rows) => _performanceMetrics.RecordRollupRows(rows);

    internal void RecordArchiveDuration(long started) => _performanceMetrics.RecordArchiveDuration(started);

    internal void RecordArchiveRows(long rows) => _performanceMetrics.RecordArchiveRows(rows);

    internal void RecordRetentionDuration(long started) => _performanceMetrics.RecordRetentionDuration(started);

    internal void RecordRetentionTablesDropped(long count) => _performanceMetrics.RecordRetentionTablesDropped(count);

    internal void RecordRetentionArchiveRecordsExpired(long count) => _performanceMetrics.RecordRetentionArchiveRecordsExpired(count);

    internal void RecordRetentionRollupRowsDeleted(long count) => _performanceMetrics.RecordRetentionRollupRowsDeleted(count);

    private async Task AcquireWriteLockAsync(CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        await _writeLock.WaitAsync(token);
        _performanceMetrics.RecordWriteLockWait(started);
    }

    /// <summary>资源失败时限频采集类别，不记录 SQL、参数或业务内容.</summary>
    internal void LogResourceFailure(Exception exception, string operation)
    {
        if (exception is not DuckDBException && exception is not OutOfMemoryException) return;
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var previous = Interlocked.Read(ref _lastResourceDiagnosticTicks);
        if (now - previous < TimeSpan.FromSeconds(30).Ticks
            || Interlocked.CompareExchange(ref _lastResourceDiagnosticTicks, now, previous) != previous) return;
        try
        {
            using var connection = CloneConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT tag, memory_usage_bytes, temporary_storage_bytes FROM duckdb_memory()";
            using var reader = command.ExecuteReader();
            var categories = new List<string>();
            while (reader.Read())
            {
                var memory = reader.GetInt64(1);
                var spill = reader.GetInt64(2);
                if (memory > 0 || spill > 0) categories.Add($"{reader.GetString(0)}={memory}/{spill}");
            }

            using var process = System.Diagnostics.Process.GetCurrentProcess();
            _logger.LogWarning("DuckDB operation failed (operation={Operation}, effective_limit={EffectiveLimit}, private_bytes={PrivateBytes}, categories={MemoryCategories})",
                operation, EffectiveMemoryLimit, process.PrivateMemorySize64, string.Join(",", categories));
        }
        catch (Exception diagnosticFailure)
        {
            _logger.LogWarning("DuckDB resource diagnostics unavailable (operation={Operation}, error_type={ErrorType})",
                operation, diagnosticFailure.GetType().Name);
        }
    }

    internal void InvalidateLogColumns(string table) => _logColumns.TryRemove(table, out _);

    /// <summary>logs 数据表两种合法列布局：旧版物理宽表（resource 原文）与视图门面窄表（resource_id 字典编码）.</summary>
    internal static readonly string[] WideLogColumns = ["id", "ts", "service", "severity", "body", "trace_id", "span_id", "attributes", "resource", "source"];
    internal static readonly string[] NarrowLogColumns = ["id", "ts", "service", "severity", "body", "trace_id", "span_id", "attributes", "resource_id", "source"];

    internal async Task<IReadOnlyList<string>> GetLogColumnsAsync(DuckDBConnection connection, string table, CancellationToken token)
    {
        if (_logColumns.TryGetValue(table, out var columns))
        {
            return columns;
        }

        // 表名由 TimeTables 生成；只准备空投影，避免每批扫描、排序整个系统目录。
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} LIMIT 0";
        using var reader = await command.ExecuteReaderAsync(token);
        columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        if (!MatchesLayout(columns, WideLogColumns) && !MatchesLayout(columns, NarrowLogColumns))
        {
            throw new InvalidOperationException("日志表列布局不受支持，请检查表结构");
        }

        _logColumns[table] = columns;
        return columns;
    }

    private static bool MatchesLayout(IReadOnlyList<string> columns, string[] expected)
    {
        return columns.Count == expected.Length && columns.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    internal void HidePublishedSnapshot(string table) => _publishedSnapshots.TryAdd(table, 0);

    internal async Task<string?> FreezeDayAsync(string prefix, DateTimeOffset day, CancellationToken token)
    {
        var table = TimeTables.Name(prefix, day);
        using var tableLease = await LockTableAsync(table, token);
        using var sourcesLease = await ChangeSourcesAsync(token);
        if (!_tables.ContainsKey(table))
        {
            return null;
        }

        if (TimeTables.IsViewBacked(prefix) && _views.ContainsKey(table))
        {
            // 视图门面天：伴生物理表整体改名为快照 + 视图重建为宽列快照（归档 COPY/词项索引零改动）。
            // 四条 DDL 在一个事务内原子提交，中断即整体回滚，不会留下指向已改名表的坏视图
            var (snapshot, dataSnapshot, resSnapshot) = SnapshotNames(prefix, day);
            var dataTable = TimeTables.DataTableName(prefix, day);
            var resTable = TimeTables.ResTableName(prefix, day);
            using var writeLease = await LockWriteAsync(token);
            using var connection = CloneConnection();
            using var transaction = connection.BeginTransaction();
            await ExecuteInTransactionAsync(connection, transaction, $"ALTER TABLE {dataTable} RENAME TO {dataSnapshot}", token);
            await ExecuteInTransactionAsync(connection, transaction, $"ALTER TABLE {resTable} RENAME TO {resSnapshot}", token);
            await ExecuteInTransactionAsync(connection, transaction, $"DROP VIEW {table}", token);
            await ExecuteInTransactionAsync(connection, transaction, TimeTables.ViewSql(snapshot, dataSnapshot, resSnapshot, prefix).Replace("IF NOT EXISTS ", string.Empty, StringComparison.Ordinal), token);
            transaction.Commit();
            _tables.TryRemove(table, out _);
            _views.TryRemove(table, out _);
            _tables.TryRemove(dataTable, out _);
            _tables.TryRemove(resTable, out _);
            InvalidateLogColumns(table);
            InvalidateLogColumns(dataTable);
            _tables.TryAdd(snapshot, 0);
            _views.TryAdd(snapshot, 0);
            _tables.TryAdd(dataSnapshot, 0);
            _tables.TryAdd(resSnapshot, 0);
            return snapshot;
        }

        var snapshotName = TimeTables.ArchiveName(prefix, day);
        if (prefix == TimeTables.TracesPrefix)
        {
            await ExecuteWriteAsync($"DROP INDEX IF EXISTS {table}_tid_idx", cancellationToken: token);
        }

        await ExecuteWriteAsync($"ALTER TABLE {table} RENAME TO {snapshotName}", cancellationToken: token);
        _tables.TryRemove(table, out _);
        InvalidateLogColumns(table);
        _tables.TryAdd(snapshotName, 0);
        return snapshotName;
    }

    /// <summary>视图门面天的冻结快照三元组（同一 guid，命名与 ArchiveNames 规则一致）.</summary>
    private static (string View, string Data, string Res) SnapshotNames(string prefix, DateTimeOffset day)
    {
        var guid = Guid.NewGuid().ToString("N");
        var dayString = day.UtcDateTime.ToString(TimeTables.DayFormat, System.Globalization.CultureInfo.InvariantCulture);
        var view = $"{prefix}archive_{guid}_{dayString}";
        var (data, res) = TimeTables.ArchiveCompanions(prefix, view);
        return (view, data, res);
    }

    /// <summary>在指定连接的事务内执行 DDL（冻结/结构变更原子化用；表名与 SQL 均为内部生成）.</summary>
    private static async Task<int> ExecuteInTransactionAsync(DuckDBConnection connection, DbTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DuckDbConnectionManager"/> class.
    /// </summary>
    /// <param name="options">系统配置.</param>
    /// <param name="logger">日志.</param>
    public DuckDbConnectionManager(SystemOptions options, ILogger<DuckDbConnectionManager> logger)
    {
        _options = options.DuckDb;
        // 初始查询准入门；InitializeAsync 以 TuningState 基线覆盖
        _queryLock = new SemaphoreSlim(4, 4);
        _s3 = options.S3;
        _traceIndexEnabled = options.Ingest.TraceIndexEnabled;
        _logger = logger;
    }

    /// <summary>WAL checkpoint 阈值（压测择优）：调大可减少高频提交时 checkpoint 抢占 commit.</summary>
    private const string CheckpointThreshold = "1GB";

    /// <summary>
    /// 初始化：打开主连接、应用运行参数（来自 ITuningState 自动调控基线）、加载表清单.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <param name="memoryLimit">内存限制（TuningState 基线）.</param>
    /// <param name="threads">线程数（TuningState 基线）.</param>
    /// <param name="maxConcurrentQueries">并发查询数（TuningState 基线）.</param>
    /// <returns>Task.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default, string? memoryLimit = null, int? threads = null, int? maxConcurrentQueries = null)
    {
        var dbPath = Path.Combine(AppConst.GetPath(), _options.Path.Replace('\\', '/'));
        var dbDirectory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
        }

        _master = new DuckDBConnection($"Data Source={dbPath}");
        await _master.OpenAsync(cancellationToken);

        await ApplyMemoryLimitAsync(memoryLimit ?? TuningDefaults.MemoryLimit, cancellationToken);
        var effectiveThreads = threads ?? TuningDefaults.Threads;
        if (effectiveThreads > 0)
        {
            await ApplyThreadsAsync(effectiveThreads, cancellationToken);
        }

        // WAL checkpoint 阈值：调大可减少高频提交时 checkpoint 抢占 commit
        await ExecuteUnsafeAsync("SET checkpoint_threshold = $threshold", [new DuckDBParameter("threshold", CheckpointThreshold)], cancellationToken);

        if (maxConcurrentQueries is { } queries)
        {
            ApplyMaxConcurrentQueries(queries);
        }

        if (!string.IsNullOrEmpty(_options.TempDirectory))
        {
            var tempDirectory = Path.Combine(AppConst.GetPath(), _options.TempDirectory.Replace('\\', '/'));
            Directory.CreateDirectory(tempDirectory);
            await ExecuteUnsafeAsync($"SET temp_directory='{tempDirectory.Replace("'", "''")}'");
        }

        // 小内存部署优化：大批量写入不保序，降低内存占用
        await ExecuteUnsafeAsync("SET preserve_insertion_order=false");

        // S3 冷存储（契约 C4）：httpfs 静态链接于 DuckDB.NET.Data.Full，CREATE SECRET 可直接使用；
        // Endpoint 为空 = 关闭冷层，完全跳过（零行为变化）
        await ApplyS3SecretAsync(_s3, cancellationToken);

        // 缓存已有表名与视图名（logs/traces 天对象自视图门面改造后为视图；旧宽表仍为物理表）
        await foreach (var table in QueryEachAsync("SELECT table_name FROM duckdb_tables()", cancellationToken))
        {
            _tables.TryAdd(table, 0);
        }

        await foreach (var view in QueryEachAsync("SELECT view_name FROM duckdb_views() WHERE internal = false", cancellationToken))
        {
            _tables.TryAdd(view, 0);
            _views.TryAdd(view, 0);
        }

        await using var config = _master.CreateCommand();
        config.CommandText = "SELECT current_setting(\'threads\')";
        var readThreads = Convert.ToInt32(await config.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        _logger.LogInformation("DuckDB initialized at {Path} with {TableCount} tables (memory_limit={MemoryLimit}, effective_limit={EffectiveLimit}, threads={Threads}, max_queries={MaxQueries}, checkpoint_threshold={CheckpointThreshold})",
            dbPath, _tables.Count, MemoryLimit, EffectiveMemoryLimit, readThreads, Math.Clamp(maxConcurrentQueries ?? TuningDefaults.MaxConcurrentQueries, 1, 32), CheckpointThreshold);
    }

    /// <inheritdoc/>
    public async Task ApplyMemoryLimitAsync(string memoryLimit, CancellationToken cancellationToken)
    {
        if (!DuckDbMemoryLimit.TryNormalize(memoryLimit, out var normalized))
        {
            throw new ArgumentException("DuckDB 内存限制格式无效", nameof(memoryLimit));
        }

        using var lease = await LockWriteAsync(cancellationToken);
        await ExecuteUnsafeAsync("SET memory_limit = $limit", [new DuckDBParameter("limit", normalized)], cancellationToken);
        // SET 已完成后不再响应请求取消，确保读回并发布实际运行值。
        await using var command = _master!.CreateCommand();
        command.CommandText = "SELECT current_setting('memory_limit')";
        var effective = Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None), System.Globalization.CultureInfo.InvariantCulture)!;
        Volatile.Write(ref _memory, new MemoryState(normalized, effective));
    }

    /// <inheritdoc/>
    public async Task ApplyThreadsAsync(int threads, CancellationToken cancellationToken)
    {
        // 线程数是实例级全局设置，写门内执行即可覆盖后续所有连接的查询与写入
        var limit = Math.Clamp(threads, 1, 64);
        using var lease = await LockWriteAsync(cancellationToken);
        await ExecuteUnsafeAsync("SET threads = $threads", [new DuckDBParameter("threads", limit)], cancellationToken);
    }

    /// <inheritdoc/>
    public void ApplyMaxConcurrentQueries(int maxConcurrentQueries)
    {
        var limit = Math.Clamp(maxConcurrentQueries, 1, 32);
        // 替换准入门：已准入的查询仍释放旧门，切换瞬间可能短暂超出新上限，在途查询结束后收敛
        Volatile.Write(ref _queryLock, new SemaphoreSlim(limit, limit));
    }

    /// <summary>
    /// 打开一个新连接（DuckDB 实例缓存保证同进程同路径共享同一数据库实例）.
    /// 注意：不要把 DuckDB 连接池化复用——流式查询后即使释放 reader/command，
    /// 连接对象保持打开仍会在实例上留下未结束的事务上下文，阻塞 CHECKPOINT/归档.
    /// </summary>
    /// <returns>已打开的连接.</returns>
    public DuckDBConnection CloneConnection()
    {
        var clone = new DuckDBConnection(_master!.ConnectionString);
        clone.Open();
        return clone;
    }

    /// <summary>
    /// 按给定 S3 配置重建冷存储 SECRET（Endpoint/Bucket 为空时跳过 = 关闭冷层）.
    /// 初始化时按文件配置调用；设置页保存 S3 后由 <see cref="S3SecretManager"/> 调用实现立即生效.
    /// </summary>
    /// <param name="s3">S3 配置.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    public async Task ApplyS3SecretAsync(S3Options s3, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(s3.Endpoint) || string.IsNullOrWhiteSpace(s3.Bucket))
        {
            return;
        }

        if (S3SecretBuilder.NormalizeEndpoint(s3.Endpoint).Length == 0)
        {
            _logger.LogWarning("S3 端点仅含协议头（{Endpoint}），无法构造 SECRET，冷层保持关闭", s3.Endpoint);
            return;
        }

        await ExecuteWriteAsync(S3SecretBuilder.BuildCreateSql("sdata_s3", s3), cancellationToken: cancellationToken);
        _logger.LogInformation("S3 SECRET created for cold storage (bucket={Bucket}, endpoint={Endpoint})", s3.Bucket, s3.Endpoint);
    }

    /// <summary>
    /// 按天确保表存在（建表 + 建索引），并缓存.
    /// logs/traces 为视图门面三件套（数据窄表 + resource 字典表 + 宽列视图）；
    /// 已存在的旧版物理宽表保持原样（跳过），由写入端按列布局自适应.
    /// </summary>
    /// <param name="prefix">表前缀.</param>
    /// <param name="days">需要的天.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    public async Task EnsureDayTablesAsync(string prefix, IEnumerable<DateTimeOffset> days, CancellationToken cancellationToken = default)
    {
        foreach (var day in days)
        {
            var table = TimeTables.Name(prefix, day);
            if (_tables.ContainsKey(table))
            {
                continue;
            }

            await AcquireWriteLockAsync(cancellationToken);
            try
            {
                if (_tables.ContainsKey(table))
                {
                    continue;
                }

                foreach (var sql in TimeTables.CreateSql(table, _traceIndexEnabled))
                {
                    await ExecuteUnsafeAsync(sql, null, cancellationToken);
                }

                InvalidateLogColumns(table);
                _tables.TryAdd(table, 0);
                if (TimeTables.IsViewBacked(prefix))
                {
                    _views.TryAdd(table, 0);
                    _tables.TryAdd(TimeTables.DataTableName(prefix, day), 0);
                    _tables.TryAdd(TimeTables.ResTableName(prefix, day), 0);
                }

                _logger.LogDebug("Created DuckDB table {Table}", table);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }

    /// <summary>该天对象是否为视图门面（数据在伴生窄表 + resource 字典表）；false = 旧版物理宽表.</summary>
    internal bool IsViewDay(string table) => _views.ContainsKey(table);

    /// <summary>
    /// 获取时间范围内已存在的按天表名.
    /// </summary>
    /// <param name="prefix">表前缀.</param>
    /// <param name="start">开始（含）.</param>
    /// <param name="end">结束（不含）.</param>
    /// <returns>表名列表（按天升序）.</returns>
    public IReadOnlyList<string> GetDayTables(string prefix, DateTimeOffset start, DateTimeOffset end)
    {
        var tables = new List<string>();
        for (var day = start.UtcDateTime.Date; day < end.UtcDateTime; day = day.AddDays(1))
        {
            var table = TimeTables.Name(prefix, new DateTimeOffset(day, TimeSpan.Zero));
            if (_tables.ContainsKey(table))
            {
                tables.Add(table);
            }
        }

        tables.AddRange(GetManagedTables(prefix).Where(table => TimeTables.IsArchive(table)
            && !_publishedSnapshots.ContainsKey(table)
            && TimeTables.ParseDay(table) is { } day && day < end && day.AddDays(1) > start));
        return tables;
    }

    /// <summary>
    /// 写锁内执行（DDL/写入）.
    /// </summary>
    /// <param name="sql">SQL.</param>
    /// <param name="parameters">参数.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>受影响行数.</returns>
    public async Task<int> ExecuteWriteAsync(string sql, IReadOnlyCollection<DuckDBParameter>? parameters = null, CancellationToken cancellationToken = default)
    {
        await AcquireWriteLockAsync(cancellationToken);
        try
        {
            return await ExecuteUnsafeAsync(sql, parameters, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 查询准入后在专属连接执行，由 mapper 聚合结果（不与后台写共享主连接）.
    /// </summary>
    /// <typeparam name="T">结果类型.</typeparam>
    /// <param name="sql">SQL.</param>
    /// <param name="parameters">参数.</param>
    /// <param name="mapper">行映射.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>聚合结果.</returns>
    public async Task<T> QueryAsync<T>(string sql, IReadOnlyCollection<DuckDBParameter>? parameters, Func<DbDataReader, Task<T>> mapper, CancellationToken cancellationToken = default)
    {
        using var queryLease = await LockQueryAsync(cancellationToken);
        await using var connection = CloneConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        ApplyParameters(command, parameters);
        command.UseStreamingMode = true;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await mapper(reader);
    }

    /// <summary>
    /// 逐行读取（读主连接）.
    /// </summary>
    /// <param name="sql">SQL.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>首列字符串序列.</returns>
    public async IAsyncEnumerable<string> QueryEachAsync(string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var command = _master!.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return reader.GetString(0);
        }
    }

    /// <inheritdoc/>
    Task<long> IStoreInfo.CountTableAsync(string tableName, CancellationToken cancellationToken) => CountAsync(tableName, cancellationToken);

    /// <inheritdoc/>
    async Task<IReadOnlyDictionary<string, long>> IStoreInfo.CountTablesAsync(IReadOnlyList<(string Name, string TableName)> tables, CancellationToken cancellationToken)
    {
        // 仅统计管理器已知的表（与 CountAsync 语义一致），未知表按 0 计
        var known = tables.Where(t => _tables.ContainsKey(t.TableName)).ToList();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (name, _) in tables)
        {
            counts[name] = 0;
        }

        if (known.Count == 0)
        {
            return counts;
        }

        // 单条 UNION ALL：一次查询门获取 + 一次连接克隆，替代 N 次串行 count
        var branches = known.Select(t => $"SELECT count(*) FROM {t.TableName}");
        var sql = string.Join(" UNION ALL ", branches);
        await QueryAsync(sql, null, async reader =>
        {
            var index = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                counts[known[index].Name] = reader.GetInt64(0);
                index++;
            }
            return 0;
        }, cancellationToken);
        return counts;
    }

    /// <inheritdoc/>
    IReadOnlyList<string> IStoreInfo.GetTableNames(string prefix) => GetManagedTables(prefix);

    /// <summary>
    /// 统计表行数.
    /// </summary>
    /// <param name="tableName">表名（仅限内部按天表）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>行数.</returns>
    public async Task<long> CountAsync(string tableName, CancellationToken cancellationToken = default)
    {
        if (!_tables.ContainsKey(tableName))
        {
            return 0;
        }

        return await QueryAsync($"SELECT count(*) FROM {tableName}", null, async reader =>
        {
            await reader.ReadAsync(cancellationToken);
            return reader.GetInt64(0);
        }, cancellationToken);
    }

    /// <summary>
    /// 列出管理器已知的指定前缀表.
    /// </summary>
    /// <param name="prefix">表前缀.</param>
    /// <returns>表名列表.</returns>
    public IReadOnlyList<string> GetManagedTables(string prefix)
    {
        return _tables.Keys.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 删除按天对象（保留策略）：视图门面天连带 DROP 伴生窄表与字典表；冻结快照视图连带其伴生快照表.
    /// </summary>
    /// <param name="tableName">表名.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    public async Task DropTableAsync(string tableName, CancellationToken cancellationToken = default)
    {
        if (_views.TryRemove(tableName, out _))
        {
            await ExecuteWriteAsync($"DROP VIEW IF EXISTS {tableName}", cancellationToken: cancellationToken);
            foreach (var companion in ViewCompanionTables(tableName))
            {
                await ExecuteWriteAsync($"DROP TABLE IF EXISTS {companion}", cancellationToken: cancellationToken);
                _tables.TryRemove(companion, out _);
                InvalidateLogColumns(companion);
            }
        }
        else
        {
            await ExecuteWriteAsync($"DROP TABLE IF EXISTS {tableName}", cancellationToken: cancellationToken);
        }

        _tables.TryRemove(tableName, out _);
        InvalidateLogColumns(tableName);
        _logger.LogInformation("Dropped expired DuckDB table {Table}", tableName);
    }

    /// <summary>
    /// 推导视图对象的伴生物理表名（天对象或冻结快照；logs/traces 前缀互逆规则，见 TimeTables.ArchiveCompanions）.
    /// </summary>
    /// <param name="viewName">视图名.</param>
    /// <returns>伴生表名列表.</returns>
    private IReadOnlyList<string> ViewCompanionTables(string viewName)
    {
        foreach (var prefix in new[] { TimeTables.LogsPrefix, TimeTables.TracesPrefix })
        {
            if (viewName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (TimeTables.IsArchive(viewName))
                {
                    var (data, res) = TimeTables.ArchiveCompanions(prefix, viewName);
                    return [data, res];
                }

                var day = TimeTables.ParseDay(viewName);
                if (day != null)
                {
                    return [TimeTables.DataTableName(prefix, day.Value), TimeTables.ResTableName(prefix, day.Value)];
                }
            }
        }

        return [];
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _alertQueryLock.WaitAsync();
        await _maintenanceLock.WaitAsync();
        await _writeLock.WaitAsync();
        try
        {
            if (_master != null)
            {
                await _master.DisposeAsync();
                _master = null;
            }
        }
        finally
        {
            _writeLock.Release();
            _writeLock.Dispose();
            _maintenanceLock.Release();
            _maintenanceLock.Dispose();
            _alertQueryLock.Release();
            _alertQueryLock.Dispose();
            Volatile.Read(ref _queryLock).Dispose();
        }
    }

    private static void ApplyParameters(DuckDBCommand command, IReadOnlyCollection<DuckDBParameter>? parameters)
    {
        if (parameters == null)
        {
            return;
        }

        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }
    }

    private async Task<int> ExecuteUnsafeAsync(string sql, IReadOnlyCollection<DuckDBParameter>? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var command = _master!.CreateCommand();
        command.CommandText = sql;
        ApplyParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
