using System.Globalization;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SData.Database;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Settings;
using SData.Store.QueryDsl;

namespace SData.Store;

/// <summary>
/// 保留策略后台服务（T512 三段保留）：热（原始表）→ 冷（归档后保留至 hot+cold）→ 过期 DROP.
/// 冷层开启（ColdDays&gt;0）时只 DROP「已落冷存档台账且超过 hot+cold」的天表——先归档后删除，绝不丢数据；
/// 冷层关闭时保持旧行为（超 hot 天数直接 DROP）.
/// </summary>
public class RetentionService : BackgroundService
{
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly SystemOptions _systemOptions;
    private readonly ISystemSettingsProvider _settingsProvider;
    private readonly IObjectStore _objectStore;
    private readonly ColdCacheService _coldCache;
    private readonly ColdLedgerCache _ledgerCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetentionService"/> class.
    /// </summary>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="systemOptions">系统配置（旧形态保留回退）.</param>
    /// <param name="settingsProvider">运行时系统设置（DB 覆盖 &gt; 文件回退）.</param>
    /// <param name="objectStore">对象存储（S3/本地冷存删除通道）.</param>
    /// <param name="coldCache">冷数据本地缓存（对象过期后失效对应缓存文件）.</param>
    /// <param name="ledgerCache">台账内存镜像（删除台账后刷新）.</param>
    /// <param name="scopeFactory">scope 工厂（SQLite 冷存档台账）.</param>
    /// <param name="logger">日志.</param>
    public RetentionService(DuckDbConnectionManager connectionManager, SystemOptions systemOptions, ISystemSettingsProvider settingsProvider, IObjectStore objectStore, ColdCacheService coldCache, ColdLedgerCache ledgerCache, IServiceScopeFactory scopeFactory, ILogger<RetentionService> logger)
    {
        _connectionManager = connectionManager;
        _systemOptions = systemOptions;
        _settingsProvider = settingsProvider;
        _objectStore = objectStore;
        _coldCache = coldCache;
        _ledgerCache = ledgerCache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 启动后先等 1 分钟，让建库流程完成
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ApplyRetentionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            catch (Exception exception)
            {
                _logger.LogError(exception, "Retention cleanup failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 单轮清理（内部方法，测试直调）.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await ApplyRetentionAsync(cancellationToken);
    }

    private async Task ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var maintenanceLease = await _connectionManager.LockMaintenanceAsync(cancellationToken);
            await DropAndExpireAsync(cancellationToken);

            // 汇总表清理与 CHECKPOINT 不改变数据源目录，移出交接锁，避免等待并阻塞在途查询
            var (metricHotDays, metricColdDays) = ArchiveService.ResolveRetention(DataDomain.Metrics, _settingsProvider.GetRetention(), _systemOptions.DuckDb);
            var rollupDays = metricHotDays <= 0 ? 0 : metricHotDays + Math.Max(0, metricColdDays);
            await PruneRollupsAsync(_connectionManager, DateTimeOffset.UtcNow, rollupDays, cancellationToken);

            // CHECKPOINT 收缩 WAL
            await _connectionManager.ExecuteMaintenanceAsync("CHECKPOINT", cancellationToken: cancellationToken);
        }
        finally
        {
            _connectionManager.RecordRetentionDuration(started);
        }
    }

    private async Task DropAndExpireAsync(CancellationToken cancellationToken)
    {
        var archived = await LoadArchivedRowsAsync(cancellationToken);
        var expired = SelectExpiredColdObjects(archived);
        using (await _connectionManager.ChangeSourcesAsync(cancellationToken))
        {
            var (mqDays, _) = ArchiveService.ResolveRetention(DataDomain.Logs, _settingsProvider.GetRetention(), _systemOptions.DuckDb);
            if (mqDays > 0)
            {
                var cutoff = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).AddDays(-mqDays);
                foreach (var table in _connectionManager.GetManagedTables(TimeTables.MqConsumptionPrefix))
                {
                    if (TimeTables.ParseDay(table) is { } day && day < cutoff)
                        await DropTableAsync(table, cancellationToken);
                }
            }

            await DropExpiredAsync(DataDomain.Logs, TimeTables.LogsPrefix, cancellationToken);
            await DropExpiredAsync(DataDomain.Traces, TimeTables.TracesPrefix, cancellationToken);
            await DropExpiredAsync(DataDomain.Metrics, TimeTables.MetricsPrefix, cancellationToken);
            await DropExpiredAsync(DataDomain.Iot, TimeTables.IotPrefix, cancellationToken);
            await DropExpiredAsync(DataDomain.RumSession, TimeTables.RumSessionPrefix, cancellationToken);
            await DropExpiredAsync(DataDomain.RumEvent, TimeTables.RumEventPrefix, cancellationToken);
            await DropExpiredAsync(DataDomain.RumReplay, TimeTables.RumReplayPrefix, cancellationToken);
            _ledgerCache.Suppress(expired.Select(item => item.ObjectKey));
        }

        await ExpireColdObjectsAsync(expired, cancellationToken);
    }

    /// <summary>汇总表与指标总保留期一致；非正值表示永久保留.</summary>
    internal static async Task PruneRollupsAsync(DuckDbConnectionManager manager, DateTimeOffset now, int days, CancellationToken token)
    {
        if (days <= 0) return;
        var cutoff = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-days);
        foreach (var table in new[] { TimeTables.Rollup5mTable, TimeTables.Rollup1hTable })
        {
            var deleted = await manager.ExecuteWriteAsync($"DELETE FROM {table} WHERE bucket_ts < $cutoff",
                [new DuckDB.NET.Data.DuckDBParameter("cutoff", LogStore.ToDb(cutoff))], token);
            if (deleted > 0)
            {
                manager.RecordRetentionRollupRowsDeleted(deleted);
            }
        }
    }

    private async Task<List<ArchivedObject>> LoadArchivedRowsAsync(CancellationToken cancellationToken)
    {
        var list = new List<ArchivedObject>();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var rows = await db.ColdArchives.Select(x => new { x.Id, x.Domain, x.Day, x.ObjectKey }).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            list.Add(new ArchivedObject(row.Id, row.Domain, row.Day, row.ObjectKey));
        }

        return list;
    }

    private List<ArchivedObject> SelectExpiredColdObjects(List<ArchivedObject> archived)
    {
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        var expired = new List<ArchivedObject>();
        foreach (var item in archived)
        {
            var parsedDomain = ArchiveService.Domains.Select(x => x.Domain).Cast<DataDomain?>()
                .FirstOrDefault(x => ArchiveService.DomainName(x!.Value) == item.Domain);
            if (parsedDomain is not { } parsed)
            {
                continue;
            }

            var (hotDays, coldDays) = ArchiveService.ResolveRetention(parsed, _settingsProvider.GetRetention(), _systemOptions.DuckDb);
            if (coldDays <= 0 || !DateTimeOffset.TryParseExact(item.Day, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dayTime))
            {
                continue;
            }

            if (dayTime.UtcDateTime.Date < today.AddDays(-(hotDays + coldDays)))
            {
                expired.Add(item);
            }
        }

        return expired;
    }

    private async Task ExpireColdObjectsAsync(IReadOnlyList<ArchivedObject> archived, CancellationToken cancellationToken)
    {
        var pending = archived.Select(item => item.ObjectKey).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, domain, _, objectKey) in archived)
        {
            var deletedPrimary = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parsed = ArchiveService.Domains
                    .Select(x => x.Domain)
                    .First(x => ArchiveService.DomainName(x) == domain);
                var idxKey = ColdTermIndex.ToIdxKey(objectKey);
                if (parsed == DataDomain.Logs && !string.Equals(idxKey, objectKey, StringComparison.Ordinal))
                {
                    await _objectStore.DeleteAsync(idxKey, cancellationToken);
                }

                var normalizedKey = objectKey.Replace("\\", "/");
                var parent = normalizedKey.Split("/")[^2];
                var shard = Path.GetFileNameWithoutExtension(normalizedKey);
                if (Guid.TryParseExact(parent, "N", out _))
                {
                    shard = parent + "_" + shard;
                }

                var idxMirror = ColdTermIndex.LocalMirrorPath(ArchiveService.DomainName(parsed), shard);
                if (File.Exists(idxMirror))
                {
                    File.Delete(idxMirror);
                }

                _coldCache.Invalidate(objectKey);
                await _objectStore.DeleteAsync(objectKey, cancellationToken);
                deletedPrimary = true;

                // 主对象已删除后不再响应取消；必须尽快提交台账删除，避免进程取消留下指向空对象的行。
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
                await db.ColdArchives.Where(x => x.Id == id).ExecuteDeleteAsync(CancellationToken.None);
                _connectionManager.RecordRetentionArchiveRecordsExpired(1);
                pending.Remove(objectKey);
            }
            catch (OperationCanceledException)
            {
                foreach (var pendingKey in pending)
                {
                    _ledgerCache.Restore(pendingKey);
                }

                throw;
            }
            catch (Exception exception)
            {
                if (deletedPrimary)
                {
                    _logger.LogError(exception, "冷存主对象已删除但台账提交失败，保持隐藏并待下轮修复：{ObjectKey}", objectKey);
                }
                else
                {
                    _ledgerCache.Restore(objectKey);
                    _logger.LogWarning(exception, "冷存对象删除失败，恢复查询并保留台账待下轮重试：{ObjectKey}", objectKey);
                }

                pending.Remove(objectKey);
                continue;
            }
        }

        // 始终刷新：也可收敛上轮主对象已删、台账提交失败后遗留的隐藏项。
        await _ledgerCache.RefreshAsync(CancellationToken.None);
    }

    private async Task DropExpiredAsync(DataDomain domain, string prefix, CancellationToken cancellationToken)
    {
        var (hotDays, coldDays) = ArchiveService.ResolveRetention(domain, _settingsProvider.GetRetention(), _systemOptions.DuckDb);
        if (hotDays <= 0)
        {
            return;
        }

        var today = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        var cutoff = coldDays > 0 ? today.AddDays(-(hotDays + coldDays)) : today.AddDays(-hotDays);
        foreach (var table in _connectionManager.GetManagedTables(prefix))
        {
            var day = TimeTables.ParseDay(table);
            if (day == null || day.Value >= cutoff)
            {
                continue;
            }

            // 冷层开启时由归档服务清理已发布批次；绝不删除未发布的迟到数据。
            if (coldDays > 0 || TimeTables.IsArchive(table))
            {
                continue;
            }

            await DropTableAsync(table, cancellationToken);
        }
    }

    private async Task DropTableAsync(string table, CancellationToken cancellationToken)
    {
        await _connectionManager.DropTableAsync(table, cancellationToken);
        _connectionManager.RecordRetentionTablesDropped(1);
    }

    private sealed record ArchivedObject(long Id, string Domain, string Day, string ObjectKey);
}
