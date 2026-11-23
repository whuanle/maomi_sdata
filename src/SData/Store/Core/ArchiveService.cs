using System.Globalization;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SData.Database;
using SData.Database.Entities;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Settings;
using SData.Store.QueryDsl;

namespace SData.Store;

/// <summary>
/// 冷存档后台服务（T512）：把超过热存期且未归档的天表 COPY TO parquet → 行数校验 → 落台账 → DROP + CHECKPOINT.
/// 冻结批次独立归档；失败保留本地批次重试，迟到数据追加新批次，台账发布后清理热副本。
/// 双写并存（本地磁盘冷备）：本地冷存目录（data/cold/{domain}/{day[_HH]}.parquet）恒有副本且与 S3 桶内结构同构，
/// 配置 S3 时每分片追写 s3:// 副本并各落一行台账；未配置 S3 时仅本地（与 T510 查询通配兼容）。
/// 任一目的地失败整轮失败不落台账（全有或全无），下轮覆盖半写重试.
/// </summary>
public class ArchiveService : BackgroundService
{
    /// <summary>归档循环间隔.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(10);

    /// <summary>归档域（RUM 三表族共享 Rum 保留策略）.</summary>
    internal static readonly (DataDomain Domain, string Prefix)[] Domains =
    [
        (DataDomain.Logs, TimeTables.LogsPrefix),
        (DataDomain.Traces, TimeTables.TracesPrefix),
        (DataDomain.Metrics, TimeTables.MetricsPrefix),
        (DataDomain.Iot, TimeTables.IotPrefix),
        (DataDomain.RumSession, TimeTables.RumSessionPrefix),
        (DataDomain.RumEvent, TimeTables.RumEventPrefix),
        (DataDomain.RumReplay, TimeTables.RumReplayPrefix),
    ];

    private readonly DuckDbConnectionManager _connectionManager;
    private readonly SystemOptions _systemOptions;
    private readonly ISystemSettingsProvider _settingsProvider;
    private readonly ColdLedgerCache _ledgerCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ArchiveService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArchiveService"/> class.
    /// </summary>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="systemOptions">系统配置（旧形态保留回退）.</param>
    /// <param name="settingsProvider">运行时系统设置（DB 覆盖 &gt; 文件回退）.</param>
    /// <param name="ledgerCache">台账内存镜像（落台账后刷新，查询路径零 SQLite 往返）.</param>
    /// <param name="scopeFactory">scope 工厂（SQLite 台账）.</param>
    /// <param name="logger">日志.</param>
    public ArchiveService(DuckDbConnectionManager connectionManager, SystemOptions systemOptions, ISystemSettingsProvider settingsProvider, ColdLedgerCache ledgerCache, IServiceScopeFactory scopeFactory, ILogger<ArchiveService> logger)
    {
        _connectionManager = connectionManager;
        _systemOptions = systemOptions;
        _settingsProvider = settingsProvider;
        _ledgerCache = ledgerCache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Archive service started (tick={Tick})", TickInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Archive sweep failed");
            }
        }

        _logger.LogInformation("Archive service stopped");
    }

    /// <summary>
    /// 单轮归档（内部方法，测试直调）.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>本轮归档的天表数.</returns>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var maintenanceLease = await _connectionManager.LockMaintenanceAsync(cancellationToken);
            var archived = 0;
            var today = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
            foreach (var (domain, prefix) in Domains)
            {
                var (hotDays, coldDays) = ResolveRetention(domain);
                if (coldDays <= 0 || hotDays <= 0)
                {
                    continue;
                }

                // 冻结批次先恢复，完成后再处理迟到数据重建的热表。
                var cutoff = today.AddDays(-hotDays);
                foreach (var candidate in _connectionManager.GetManagedTables(prefix).OrderByDescending(TimeTables.IsArchive))
                {
                    var table = candidate;
                    var day = TimeTables.ParseDay(table);
                    if (day == null || day.Value > cutoff)
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TimeTables.IsArchive(table))
                    {
                        table = await _connectionManager.FreezeDayAsync(prefix, day.Value, cancellationToken);
                        if (table == null)
                        {
                            continue;
                        }
                    }

                    var archivedRows = await ArchiveAsync(domain, table, day.Value, cancellationToken);
                    _connectionManager.RecordArchiveRows(archivedRows);
                    archived++;
                }
            }

            return archived;
        }
        finally
        {
            _connectionManager.RecordArchiveDuration(started);
        }
    }

    private async Task<long> ArchiveAsync(DataDomain domain, string table, DateTimeOffset day, CancellationToken cancellationToken)
    {
        var batchId = TimeTables.ArchiveId(table);
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            var marker = "/" + batchId + "/";
            var domainName = DomainName(domain);
            if (await db.ColdArchives.AnyAsync(x => x.Domain == domainName && x.ObjectKey.Contains(marker), cancellationToken))
            {
                using var handoff = await _connectionManager.ChangeSourcesAsync(cancellationToken);
                await _ledgerCache.RefreshAsync(cancellationToken);
                _connectionManager.HidePublishedSnapshot(table);
                await _connectionManager.DropTableAsync(table, cancellationToken);
                return 0;
            }
        }

        var rowCount = await _connectionManager.QueryAsync(
            $"SELECT count(*) FROM {table}",
            null,
            async reader =>
            {
                await reader.ReadAsync(cancellationToken);
                return reader.GetInt64(0);
            }, cancellationToken);
        if (rowCount == 0)
        {
            var dayString = FormatDay(day);
            var emptyRows = new List<(string ObjectKey, long RowCount)> { (BatchObjectKey(LocalArchiveKey(domain, dayString), batchId), 0L) };
            var emptyS3Key = S3ArchiveKey(domain, dayString, _settingsProvider.GetS3());
            if (emptyS3Key != null)
            {
                emptyRows.Add((BatchObjectKey(emptyS3Key, batchId), 0L));
            }

            using var handoff = await _connectionManager.ChangeSourcesAsync(cancellationToken);
            await SaveLedgerRowsAsync(domain, day, emptyRows, cancellationToken);
            _connectionManager.HidePublishedSnapshot(table);
            await _connectionManager.DropTableAsync(table, cancellationToken);
            return 0;
        }

        var s3 = _settingsProvider.GetS3();
        var codec = _settingsProvider.GetCompression().ArchiveParquetCodec;
        var hourly = IsHourly(s3.ArchiveGranularity);
        var plan = BuildShardPlan(domain, day, s3);
        var ledgerRows = new List<(string ObjectKey, long RowCount)>();
        var shardTotal = 0;

        foreach (var (shardName, localKey, s3Key) in plan)
        {
            var filter = hourly
                ? $" WHERE ts >= TIMESTAMP '{day:yyyy-MM-dd} {shardName[^2..]}:00:00' AND ts < TIMESTAMP '{day:yyyy-MM-dd} {shardName[^2..]}:00:00' + INTERVAL '1 hour'"
                : string.Empty;
            var shardCount = await _connectionManager.QueryAsync(
                $"SELECT count(*) FROM {table}{filter}",
                null,
                async reader =>
                {
                    await reader.ReadAsync(cancellationToken);
                    return reader.GetInt64(0);
                }, cancellationToken);
            if (hourly && shardCount == 0)
            {
                continue; // 小时粒度跳过空分片（不生成对象与台账行）
            }

            foreach (var objectKey in ShardObjectKeys(localKey, s3Key))
            {
                var batchKey = BatchObjectKey(objectKey, batchId);
                var destination = batchKey.StartsWith("s3://", StringComparison.Ordinal)
                    ? batchKey
                    : Path.Combine(AppConst.GetPath(), batchKey);
                PrepareDestination(destination);
                await _connectionManager.ExecuteMaintenanceAsync(
                    $"COPY (SELECT * FROM {table}{filter} ORDER BY {SortKeys(domain)}) TO '{destination.Replace("'", "''")}' (FORMAT PARQUET, COMPRESSION {codec})",
                    cancellationToken: cancellationToken);

                var archivedCount = await _connectionManager.QueryAsync(
                    $"SELECT count(*) FROM read_parquet('{destination.Replace("'", "''")}')",
                    null,
                    async reader =>
                    {
                        await reader.ReadAsync(cancellationToken);
                        return reader.GetInt64(0);
                    }, cancellationToken);
                if (archivedCount != shardCount)
                {
                    throw new InvalidOperationException($"归档行数校验失败：{table} 分片 {batchId}_{shardName} 目的地 {batchKey} 原始 {shardCount} 行，parquet {archivedCount} 行");
                }

                ledgerRows.Add((batchKey, shardCount));
            }

            // 词项索引 sidecar（T518）：本地镜像（查询候选天裁剪零网络）恒建；S3 配置时对 s3:// 分片追建远端索引。
            // 在落台账之前构建——失败则整轮归档失败（不落台账），下一轮覆盖重试，保持幂等语义
            if (domain == DataDomain.Logs)
            {
                var idxMirror = ColdTermIndex.LocalMirrorPath(DomainName(domain), batchId + "_" + shardName);
                Directory.CreateDirectory(Path.GetDirectoryName(idxMirror)!);
                await _connectionManager.ExecuteMaintenanceAsync(
                    ColdTermIndex.BuildIndexSql(table, idxMirror, codec),
                    cancellationToken: cancellationToken);
                if (s3Key != null)
                {
                    var idxKey = ColdTermIndex.ToIdxKey(BatchObjectKey(s3Key, batchId));
                    if (idxKey.StartsWith("s3://", StringComparison.Ordinal))
                    {
                        await _connectionManager.ExecuteMaintenanceAsync(
                            ColdTermIndex.BuildIndexSql(table, idxKey, codec),
                            cancellationToken: cancellationToken);
                    }
                }
            }

            shardTotal++;
        }

        using (await _connectionManager.ChangeSourcesAsync(cancellationToken))
        {
            await SaveLedgerRowsAsync(domain, day, ledgerRows, cancellationToken);
            _connectionManager.HidePublishedSnapshot(table);
            await _connectionManager.DropTableAsync(table, cancellationToken);
        }
        await _connectionManager.ExecuteMaintenanceAsync("CHECKPOINT", cancellationToken: cancellationToken);
        _logger.LogInformation("Archived {Table} → {Shards} shards / {Objects} objects ({Rows} rows, {Granularity} granularity, index mirrored)", table, shardTotal, ledgerRows.Count, rowCount, hourly ? "hour" : "day");
        return rowCount;
    }

    /// <summary>
    /// 归档分片粒度解析：仅识别 day/hour，未知值回退 day（管理员在 S3 设置选择，运行时热生效）.
    /// </summary>
    /// <param name="granularity">配置值.</param>
    /// <returns>true = 按小时分片.</returns>
    internal static bool IsHourly(string? granularity)
    {
        return string.Equals(granularity, "hour", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 归档分片计划（双写并存）：本地冷存键恒有（data/cold 与 S3 桶内结构同构：{folder}/{day[_HH]}.parquet），
    /// 配置 S3 时每分片追加分片 s3:// 副本键；day 粒度一天一分片、hour 粒度一天 24 分片（yyyyMMddHH 命名，
    /// 对象与索引同名不同后缀），空分片在归档时跳过.
    /// </summary>
    /// <param name="domain">数据域.</param>
    /// <param name="day">天.</param>
    /// <param name="s3">S3 配置（Endpoint/Bucket 为空 = 未配置，仅本地）.</param>
    /// <returns>(分片名, 本地对象键, S3 对象键或 null) 列表.</returns>
    internal static List<(string ShardName, string LocalKey, string? S3Key)> BuildShardPlan(DataDomain domain, DateTimeOffset day, S3Options s3)
    {
        var dayString = FormatDay(day);
        var list = new List<(string, string, string?)>();
        if (IsHourly(s3.ArchiveGranularity))
        {
            for (var hour = 0; hour < 24; hour++)
            {
                var shard = $"{dayString}{hour:00}";
                list.Add((shard, LocalArchiveKey(domain, shard), S3ArchiveKey(domain, shard, s3)));
            }
        }
        else
        {
            list.Add((dayString, LocalArchiveKey(domain, dayString), S3ArchiveKey(domain, dayString, s3)));
        }

        return list;
    }

    /// <summary>分片写入目的地（本地先、S3 后）：双写并存返回两键，未配置 S3 仅本地.</summary>
    /// <param name="localKey">本地对象键.</param>
    /// <param name="s3Key">S3 对象键或 null.</param>
    /// <returns>对象键序列.</returns>
    private static IEnumerable<string> ShardObjectKeys(string localKey, string? s3Key)
    {
        yield return localKey;
        if (s3Key != null)
        {
            yield return s3Key;
        }
    }

    /// <summary>本地冷存基础对象键（相对 ContentRoot；与 S3 桶内结构同构）.</summary>
    /// <param name="domain">数据域.</param>
    /// <param name="dayString">分片名（yyyyMMdd 或 yyyyMMddHH）.</param>
    /// <returns>相对路径对象键.</returns>
    private static string LocalArchiveKey(DataDomain domain, string dayString)
    {
        return Path.Combine("data", "cold", DomainFolder(domain), $"{dayString}.parquet");
    }

    /// <summary>S3 基础对象键；Endpoint/Bucket 未配置返回 null.</summary>
    /// <param name="domain">数据域.</param>
    /// <param name="dayString">分片名（yyyyMMdd 或 yyyyMMddHH）.</param>
    /// <param name="s3">S3 配置.</param>
    /// <returns>s3:// 对象键或 null.</returns>
    private static string? S3ArchiveKey(DataDomain domain, string dayString, S3Options s3)
    {
        if (string.IsNullOrWhiteSpace(s3.Endpoint) || string.IsNullOrWhiteSpace(s3.Bucket))
        {
            return null;
        }

        var prefixPath = string.IsNullOrWhiteSpace(s3.Prefix) ? string.Empty : s3.Prefix.TrimEnd('/') + "/";
        return $"s3://{s3.Bucket}/{prefixPath}{DomainFolder(domain)}/{dayString}.parquet";
    }

    /// <summary>仅本地归档准备目录；S3 URI 由 DuckDB httpfs 处理.</summary>
    /// <param name="destination">归档目标.</param>
    internal static void PrepareDestination(string destination)
    {
        if (destination.StartsWith("s3://", StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }
    }

    internal static string BatchObjectKey(string key, string batchId)
    {
        var normalized = key.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return normalized[..(slash + 1)] + batchId + "/" + normalized[(slash + 1)..];
    }

    private static string DomainFolder(DataDomain domain)
    {
        return domain switch
        {
            DataDomain.Logs => "logs",
            DataDomain.Traces => "traces",
            DataDomain.Metrics => "metrics",
            DataDomain.Iot => "iot",
            DataDomain.RumSession => "rum_session",
            DataDomain.RumEvent => "rum_event",
            DataDomain.RumReplay => "rum_replay",
            _ => throw new ArgumentOutOfRangeException(nameof(domain)),
        };
    }

    /// <summary>
    /// 归档排序键（主过滤键在前）：排序后 parquet row group min/max zonemap 对等值过滤生效，
    /// 冷数据关键字检索时 service/trace_id/name/device_key 裁剪可跳过整组行、减少 S3 传输量.
    /// </summary>
    /// <param name="domain">数据域.</param>
    /// <returns>ORDER BY 键列表.</returns>
    internal static string SortKeys(DataDomain domain)
    {
        return domain switch
        {
            DataDomain.Logs => "service, ts",
            DataDomain.Traces => "trace_id, ts",
            DataDomain.Metrics => "name, ts",
            DataDomain.Iot => "device_key, ts",
            DataDomain.RumSession or DataDomain.RumEvent => "project_key, session_id, ts",
            DataDomain.RumReplay => "project_key, session_id, ts, seq",
            _ => throw new ArgumentOutOfRangeException(nameof(domain)),
        };
    }

    private static string FormatDay(DateTimeOffset day)
    {
        return day.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 落台账（一域一天一批；一个对象键一行：day 粒度每介质一行、hour 粒度每介质多行）：
    /// 每个冻结批次追加独立对象行（双写并存时本地与 S3 各一行），已发布批次恢复时仅清理本地副本。
    /// </summary>
    /// <param name="domain">数据域.</param>
    /// <param name="day">天.</param>
    /// <param name="rows">(对象键, 行数) 列表.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    private async Task SaveLedgerRowsAsync(DataDomain domain, DateTimeOffset day, List<(string ObjectKey, long RowCount)> rows, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var domainName = DomainName(domain);
        var dayString = FormatDay(day);
        // 冻结批次使用独立对象键；迟到数据追加台账，不覆盖已发布批次。
        foreach (var (objectKey, rowCount) in rows)
        {
            db.ColdArchives.Add(new ColdArchiveEntity
            {
                Domain = domainName,
                Day = dayString,
                ObjectKey = objectKey,
                RowCount = rowCount,
                ArchivedTime = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await _ledgerCache.RefreshAsync(cancellationToken);
    }

    internal static string DomainName(DataDomain domain)
    {
        return domain switch
        {
            DataDomain.Logs => "logs",
            DataDomain.Traces => "traces",
            DataDomain.Metrics => "metrics",
            DataDomain.Iot => "iot",
            DataDomain.RumSession => "rum_session",
            DataDomain.RumEvent => "rum_event",
            DataDomain.RumReplay => "rum_replay",
            _ => throw new ArgumentOutOfRangeException(nameof(domain)),
        };
    }

    /// <summary>
    /// 保留计划解析（运行时合并：DB 覆盖 &gt; 文件；旧形态 DuckDb.Retention.{X} 兜底）.
    /// </summary>
    /// <param name="domain">数据域.</param>
    /// <returns>热存与冷存天数.</returns>
    private (int HotDays, int ColdDays) ResolveRetention(DataDomain domain)
    {
        return ResolveRetention(domain, _settingsProvider.GetRetention(), _systemOptions.DuckDb);
    }

    /// <summary>
    /// 保留计划解析（契约 C4）：新形态 HotDays &gt; 0 优先，否则回退旧形态 DuckDb.Retention.{X}（0 = 永久/不归档）.
    /// </summary>
    /// <param name="domain">数据域.</param>
    /// <param name="options">系统配置.</param>
    /// <returns>热存与冷存天数.</returns>
    internal static (int HotDays, int ColdDays) ResolveRetention(DataDomain domain, SystemOptions options)
    {
        return ResolveRetention(domain, options.Retention, options.DuckDb);
    }

    /// <summary>
    /// 保留计划解析核心：新形态 HotDays &gt; 0 优先，否则回退旧形态（0 = 永久/不归档）.
    /// </summary>
    /// <param name="domain">数据域.</param>
    /// <param name="plan">三段保留计划（可能含 DB 覆盖）.</param>
    /// <param name="legacy">旧形态保留（文件）.</param>
    /// <returns>热存与冷存天数.</returns>
    internal static (int HotDays, int ColdDays) ResolveRetention(DataDomain domain, RetentionPlanOptions plan, DuckDbOptions legacy)
    {
        var tier = domain switch
        {
            DataDomain.Logs => plan.Logs,
            DataDomain.Traces => plan.Traces,
            DataDomain.Metrics => plan.Metrics,
            DataDomain.Iot => plan.Iot,
            _ => plan.Rum,
        };
        var hot = tier.HotDays;
        if (hot <= 0)
        {
            hot = domain switch
            {
                DataDomain.Logs => legacy.Retention.Logs,
                DataDomain.Traces => legacy.Retention.Traces,
                DataDomain.Metrics => legacy.Retention.Metrics,
                DataDomain.Iot => legacy.Retention.Iot,
                _ => 0,
            };
        }

        return (hot, tier.ColdDays);
    }

    /// <summary>
    /// 保留计划解析简写（ArchiveService/RetentionService 共用；内部可见）.
    /// </summary>
    internal static class RetentionPlan
    {
        /// <summary>
        /// 解析某域的（热/冷）天数.
        /// </summary>
        /// <param name="domain">数据域.</param>
        /// <param name="options">系统配置.</param>
        /// <returns>热存与冷存天数.</returns>
        public static (int HotDays, int ColdDays) Resolve(DataDomain domain, SystemOptions options)
        {
            return ArchiveService.ResolveRetention(domain, options);
        }
    }
}
