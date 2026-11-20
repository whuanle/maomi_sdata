using System.Globalization;
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
/// 未配置 S3 时写本地冷存目录（data/cold/{domain}/{day}.parquet），与 T510 查询通配兼容（本地路径通配可查询）.
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
    private readonly SemaphoreSlim _sweepLock = new(1, 1);

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
        await _sweepLock.WaitAsync(cancellationToken);
        try
        {
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

                    await ArchiveAsync(domain, table, day.Value, cancellationToken);
                    archived++;
                }
            }

            return archived;
        }
        finally
        {
            _sweepLock.Release();
        }
    }

    private async Task ArchiveAsync(DataDomain domain, string table, DateTimeOffset day, CancellationToken cancellationToken)
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
                return;
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
            using var handoff = await _connectionManager.ChangeSourcesAsync(cancellationToken);
            await SaveLedgerRowsAsync(domain, day, [(BatchObjectKey(ResolveObjectKey(domain, day), batchId), 0L)], cancellationToken);
            _connectionManager.HidePublishedSnapshot(table);
            await _connectionManager.DropTableAsync(table, cancellationToken);
            return;
        }

        var codec = _settingsProvider.GetCompression().ArchiveParquetCodec;
        var hourly = IsHourly(_settingsProvider.GetS3().ArchiveGranularity);
        var shards = (hourly ? BuildHourShards(domain, day) : [(FormatDay(day), ResolveObjectKey(domain, day))])
            .Select(shard => (ShardName: batchId + "_" + shard.Item1, ObjectKey: BatchObjectKey(shard.Item2, batchId))).ToList();
        var ledgerRows = new List<(string ObjectKey, long RowCount)>();

        foreach (var (shardName, objectKey) in shards)
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

            var destination = objectKey.StartsWith("s3://", StringComparison.Ordinal)
                ? objectKey
                : Path.Combine(AppConst.GetPath(), objectKey);
            PrepareDestination(destination);
            await _connectionManager.ExecuteWriteAsync(
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
                throw new InvalidOperationException($"归档行数校验失败：{table} 分片 {shardName} 原始 {shardCount} 行，parquet {archivedCount} 行");
            }

            // 词项索引 sidecar（T518）：本地镜像（查询候选天裁剪零网络）+ S3 备份；
            // 在落台账之前构建——失败则整轮归档失败（不落台账），下一轮覆盖重试，保持幂等语义
            if (domain == DataDomain.Logs)
            {
                var idxMirror = ColdTermIndex.LocalMirrorPath(DomainName(domain), shardName);
                Directory.CreateDirectory(Path.GetDirectoryName(idxMirror)!);
                await _connectionManager.ExecuteWriteAsync(
                    ColdTermIndex.BuildIndexSql(table, idxMirror, codec),
                    cancellationToken: cancellationToken);
                var idxKey = ColdTermIndex.ToIdxKey(objectKey);
                if (idxKey.StartsWith("s3://", StringComparison.Ordinal))
                {
                    await _connectionManager.ExecuteWriteAsync(
                        ColdTermIndex.BuildIndexSql(table, idxKey, codec),
                        cancellationToken: cancellationToken);
                }
            }

            ledgerRows.Add((objectKey, shardCount));
        }

        using (await _connectionManager.ChangeSourcesAsync(cancellationToken))
        {
            await SaveLedgerRowsAsync(domain, day, ledgerRows, cancellationToken);
            _connectionManager.HidePublishedSnapshot(table);
            await _connectionManager.DropTableAsync(table, cancellationToken);
        }
        await _connectionManager.ExecuteWriteAsync("CHECKPOINT", cancellationToken: cancellationToken);
        _logger.LogInformation("Archived {Table} → {Shards} shards ({Rows} rows, {Granularity} granularity, index mirrored)", table, ledgerRows.Count, rowCount, hourly ? "hour" : "day");
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
    /// 小时分片计划：一天 24 个分片（yyyyMMddHH 命名，对象与索引同名不同后缀），空分片在归档时跳过.
    /// </summary>
    /// <param name="domain">数据域.</param>
    /// <param name="day">天.</param>
    /// <returns>(分片名, 对象键) 列表.</returns>
    private List<(string ShardName, string ObjectKey)> BuildHourShards(DataDomain domain, DateTimeOffset day)
    {
        var dayString = FormatDay(day);
        var folder = DomainFolder(domain);
        var s3 = _settingsProvider.GetS3();
        var useS3 = !string.IsNullOrWhiteSpace(s3.Endpoint) && !string.IsNullOrWhiteSpace(s3.Bucket);
        var prefixPath = useS3 && !string.IsNullOrWhiteSpace(s3.Prefix) ? s3.Prefix.TrimEnd('/') + "/" : string.Empty;
        var list = new List<(string, string)>();
        for (var hour = 0; hour < 24; hour++)
        {
            var objectKey = useS3
                ? $"s3://{s3.Bucket}/{prefixPath}{folder}/{dayString}{hour:00}.parquet"
                : Path.Combine("data", "cold", folder, $"{dayString}{hour:00}.parquet");
            list.Add(($"{dayString}{hour:00}", objectKey));
        }

        return list;
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

    internal string ResolveObjectKey(DataDomain domain, DateTimeOffset day)
    {
        var dayString = FormatDay(day);
        var s3 = _settingsProvider.GetS3();
        if (!string.IsNullOrWhiteSpace(s3.Endpoint) && !string.IsNullOrWhiteSpace(s3.Bucket))
        {
            var prefixPath = string.IsNullOrWhiteSpace(s3.Prefix) ? string.Empty : s3.Prefix.TrimEnd('/') + "/";
            return $"s3://{s3.Bucket}/{prefixPath}{DomainFolder(domain)}/{dayString}.parquet";
        }

        return Path.Combine("data", "cold", DomainFolder(domain), $"{dayString}.parquet");
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
    /// 落台账（一域一天一批；day 粒度一行、hour 粒度多行）：
    /// 每个冻结批次追加独立对象行，已发布批次恢复时仅清理本地副本。
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
