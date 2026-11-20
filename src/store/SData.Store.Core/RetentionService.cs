using System.Globalization;
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
        using var handoff = await _connectionManager.ChangeSourcesAsync(cancellationToken);
        var archived = await LoadArchivedRowsAsync(cancellationToken);
        var (mqDays, _) = ArchiveService.ResolveRetention(DataDomain.Logs, _settingsProvider.GetRetention(), _systemOptions.DuckDb);
        if (mqDays > 0)
        {
            var cutoff = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).AddDays(-mqDays);
            foreach (var table in _connectionManager.GetManagedTables(TimeTables.MqConsumptionPrefix))
            {
                if (TimeTables.ParseDay(table) is { } day && day < cutoff)
                    await _connectionManager.DropTableAsync(table, cancellationToken);
            }
        }

        await DropExpiredAsync(DataDomain.Logs, TimeTables.LogsPrefix, cancellationToken);
        await DropExpiredAsync(DataDomain.Traces, TimeTables.TracesPrefix, cancellationToken);
        await DropExpiredAsync(DataDomain.Metrics, TimeTables.MetricsPrefix, cancellationToken);
        await DropExpiredAsync(DataDomain.Iot, TimeTables.IotPrefix, cancellationToken);
        await DropExpiredAsync(DataDomain.RumSession, TimeTables.RumSessionPrefix, cancellationToken);
        await DropExpiredAsync(DataDomain.RumEvent, TimeTables.RumEventPrefix, cancellationToken);
        await DropExpiredAsync(DataDomain.RumReplay, TimeTables.RumReplayPrefix, cancellationToken);
        await ExpireColdObjectsAsync(archived, cancellationToken);

        // CHECKPOINT 收缩 WAL
        await _connectionManager.ExecuteWriteAsync("CHECKPOINT", cancellationToken: cancellationToken);
    }

    private async Task<List<(long Id, string Domain, string Day, string ObjectKey)>> LoadArchivedRowsAsync(CancellationToken cancellationToken)
    {
        var list = new List<(long, string, string, string)>();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var rows = await db.ColdArchives.Select(x => new { x.Id, x.Domain, x.Day, x.ObjectKey }).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            list.Add((row.Id, row.Domain, row.Day, row.ObjectKey));
        }

        return list;
    }

    private async Task ExpireColdObjectsAsync(List<(long Id, string Domain, string Day, string ObjectKey)> archived, CancellationToken cancellationToken)
    {
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        foreach (var (id, domain, day, objectKey) in archived)
        {
            var parsedDomain = ArchiveService.Domains.Select(x => x.Domain).Cast<DataDomain?>().FirstOrDefault(x => ArchiveService.DomainName(x!.Value) == domain);
            if (parsedDomain is not { } parsed)
            {
                continue;
            }

            var (hotDays, coldDays) = ArchiveService.ResolveRetention(parsed, _settingsProvider.GetRetention(), _systemOptions.DuckDb);
            if (coldDays <= 0 || !DateTimeOffset.TryParseExact(day, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dayTime))
            {
                continue;
            }

            if (dayTime.UtcDateTime.Date >= today.AddDays(-(hotDays + coldDays)))
            {
                continue;
            }

            try
            {
                // S3 与本地同一删除通道（AWSSDK.S3 / File.Delete；S3 协议删除幂等）；词项索引对象与本地镜像一并清理
                await _objectStore.DeleteAsync(objectKey, cancellationToken);
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
            }
            catch (Exception exception)
            {
                // 删除失败保留台账行（下轮重试），避免误标已删
                _logger.LogWarning(exception, "冷存对象删除失败，保留台账待下轮重试：{ObjectKey}", objectKey);
                continue;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            await db.ColdArchives.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken);
        }

        // 本轮有台账删除时刷新内存镜像（查询路径零 SQLite 往返的前提下保持一致）
        if (archived.Count > 0)
        {
            await _ledgerCache.RefreshAsync(cancellationToken);
        }
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

            await _connectionManager.DropTableAsync(table, cancellationToken);
        }
    }
}
