using System.Collections.Concurrent;
using DuckDB.NET.Data;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Store;

namespace SData.Store.Core;

/// <summary>
/// DuckDB 连接管理器（单进程持有数据库实例，写串行 + 读克隆连接）.
/// </summary>
public class DuckDbConnectionManager : IAsyncDisposable, IStoreInfo
{
    private readonly DuckDbOptions _options;
    private readonly S3Options _s3;
    private readonly ILogger<DuckDbConnectionManager> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _tables = new(StringComparer.OrdinalIgnoreCase);
    private DuckDBConnection? _master;
    private int _disposed;
    private readonly ArchiveCoordination _archiveCoordination = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tableLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _publishedSnapshots = new(StringComparer.Ordinal);

    public Task<IDisposable> ReadSourcesAsync(CancellationToken token) => _archiveCoordination.ReadAsync(token);

    internal Task<IDisposable> ChangeSourcesAsync(CancellationToken token) => _archiveCoordination.WriteAsync(token);

    internal async Task<IDisposable> LockTableAsync(string table, CancellationToken token)
    {
        var gate = _tableLocks.GetOrAdd(table, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        return new ArchiveCoordination.Lease(() => gate.Release());
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

        var snapshot = TimeTables.ArchiveName(prefix, day);
        if (prefix == TimeTables.TracesPrefix)
        {
            await ExecuteWriteAsync($"DROP INDEX IF EXISTS {table}_tid_idx", cancellationToken: token);
        }

        await ExecuteWriteAsync($"ALTER TABLE {table} RENAME TO {snapshot}", cancellationToken: token);
        _tables.TryRemove(table, out _);
        _tables.TryAdd(snapshot, 0);
        return snapshot;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DuckDbConnectionManager"/> class.
    /// </summary>
    /// <param name="options">系统配置.</param>
    /// <param name="logger">日志.</param>
    public DuckDbConnectionManager(SystemOptions options, ILogger<DuckDbConnectionManager> logger)
    {
        _options = options.DuckDb;
        _s3 = options.S3;
        _logger = logger;
    }

    /// <summary>
    /// 初始化：打开主连接、应用运行参数、加载表清单.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var dbPath = Path.Combine(AppConst.GetPath(), _options.Path.Replace('\\', '/'));
        var dbDirectory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
        }

        _master = new DuckDBConnection($"Data Source={dbPath}");
        await _master.OpenAsync(cancellationToken);

        await ExecuteUnsafeAsync($"SET memory_limit='{_options.MemoryLimit}'");
        if (_options.Threads > 0)
        {
            await ExecuteUnsafeAsync($"SET threads={_options.Threads}");
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

        // 缓存已有表名
        await foreach (var table in QueryEachAsync("SELECT table_name FROM duckdb_tables()", cancellationToken))
        {
            _tables.TryAdd(table, 0);
        }

        _logger.LogInformation("DuckDB initialized at {Path} with {TableCount} tables (memory_limit={MemoryLimit})", dbPath, _tables.Count, _options.MemoryLimit);
    }

    /// <summary>
    /// 打开一个新连接（DuckDB 实例缓存保证同进程同路径共享同一数据库实例）.
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

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                if (_tables.ContainsKey(table))
                {
                    continue;
                }

                foreach (var sql in TimeTables.CreateSql(table))
                {
                    await ExecuteUnsafeAsync(sql, null, cancellationToken);
                }

                _tables.TryAdd(table, 0);
                _logger.LogDebug("Created DuckDB table {Table}", table);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }

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
        await _writeLock.WaitAsync(cancellationToken);
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
    /// 执行查询并由 mapper 聚合结果（读主连接，DuckDB 同实例多查询安全）.
    /// </summary>
    /// <typeparam name="T">结果类型.</typeparam>
    /// <param name="sql">SQL.</param>
    /// <param name="parameters">参数.</param>
    /// <param name="mapper">行映射.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>聚合结果.</returns>
    public async Task<T> QueryAsync<T>(string sql, IReadOnlyCollection<DuckDBParameter>? parameters, Func<DbDataReader, Task<T>> mapper, CancellationToken cancellationToken = default)
    {
        await using var command = _master!.CreateCommand();
        command.CommandText = sql;
        ApplyParameters(command, parameters);
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
    /// 删除按天表（保留策略）.
    /// </summary>
    /// <param name="tableName">表名.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    public async Task DropTableAsync(string tableName, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync($"DROP TABLE IF EXISTS {tableName}", cancellationToken: cancellationToken);
        _tables.TryRemove(tableName, out _);
        _logger.LogInformation("Dropped expired DuckDB table {Table}", tableName);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
