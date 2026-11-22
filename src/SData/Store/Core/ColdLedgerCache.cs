using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SData.Database;

namespace SData.Store.Core;

/// <summary>
/// 冷存档台账内存镜像（统一查询管线的表解析真相源）：
/// 启动全量加载 ColdArchives，Archive/Retention 写台账后 Refresh，TTL 兜底重载（防外部改库）；
/// 查询路径只读内存，消除每次搜索的 SQLite 往返（表解析固定开销 &lt; 1ms）.
/// 条目按域分组、按天升序；shardName 取对象键文件名（去 .parquet 后缀）：day 粒度 = yyyyMMdd，hour 粒度 = yyyyMMddHH.
/// </summary>
public sealed class ColdLedgerCache
{
    /// <summary>快照过期时间（超龄后下一次读取触发重载）.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>单域台账条目（day = 过期桶；shardName = 分片名；rowCount = 归档行数，0 = 空表无对象）.</summary>
    public sealed record ShardEntry(DateTimeOffset Day, string ShardName, string ObjectKey, long RowCount);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ColdLedgerCache> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _suppressedObjectKeys = new(StringComparer.Ordinal);
    private volatile Dictionary<string, List<ShardEntry>> _snapshot = new(StringComparer.Ordinal);
    private DateTimeOffset _loadedAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="ColdLedgerCache"/> class.
    /// </summary>
    /// <param name="scopeFactory">scope 工厂（SQLite 台账）.</param>
    /// <param name="logger">日志.</param>
    public ColdLedgerCache(IServiceScopeFactory scopeFactory, ILogger<ColdLedgerCache> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// 读取某域台账条目（快照过期时自动重载；天升序）.
    /// </summary>
    /// <param name="domainName">域名（logs/traces/metrics/iot）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>条目列表（可能为空）.</returns>
    public async Task<IReadOnlyList<ShardEntry>> GetAsync(string domainName, CancellationToken cancellationToken = default)
    {
        if (IsStale)
        {
            await ReloadAsync(cancellationToken);
        }

        return _snapshot.TryGetValue(domainName, out var list)
            ? FilterSuppressed(list)
            : Array.Empty<ShardEntry>();
    }

    /// <summary>
    /// 判断域台账是否有条目（含保留任务正在删除、暂时隐藏的条目）.
    /// </summary>
    /// <param name="domainName">域名.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>台账至少一条时返回 true.</returns>
    internal async Task<bool> HasEntriesAsync(string domainName, CancellationToken cancellationToken = default)
    {
        if (IsStale)
        {
            await ReloadAsync(cancellationToken);
        }

        return _snapshot.TryGetValue(domainName, out var list) && list.Count > 0;
    }

    /// <summary>保留删除开始前隐藏对象，防止锁外删除时新查询解析到即将过期的对象.</summary>
    /// <param name="objectKeys">需要隐藏的对象键.</param>
    internal void Suppress(IEnumerable<string> objectKeys)
    {
        foreach (var objectKey in objectKeys)
        {
            _suppressedObjectKeys.TryAdd(objectKey, 0);
        }
    }

    /// <summary>对象删除失败时恢复查询可见性；成功对象等待台账刷新后由缺失项自动解除隐藏.</summary>
    /// <param name="objectKey">需要恢复的对象键.</param>
    internal void Restore(string objectKey) => _suppressedObjectKeys.TryRemove(objectKey, out _);

    /// <summary>
    /// 强制重载快照（Archive/Retention 写台账后调用；并发调用合并为一次加载）.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await ReloadAsync(cancellationToken, force: true);
    }

    private bool IsStale => _loadedAt == default || DateTimeOffset.UtcNow - _loadedAt >= Ttl;

    private async Task ReloadAsync(CancellationToken cancellationToken, bool force = false)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            // 双检：等锁期间可能已被并发线程刷新
            if (!force && !IsStale)
            {
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            var rows = await db.ColdArchives
                .Select(x => new { x.Domain, x.Day, x.ObjectKey, x.RowCount })
                .ToListAsync(cancellationToken);

            var snapshot = new Dictionary<string, List<ShardEntry>>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var shardName = ShardNameFromKey(row.ObjectKey);
                if (shardName == null)
                {
                    continue;
                }

                if (!DateTimeOffset.TryParseExact(row.Day, TimeTables.DayFormat, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var day))
                {
                    continue;
                }

                if (!snapshot.TryGetValue(row.Domain, out var list))
                {
                    list = new List<ShardEntry>();
                    snapshot[row.Domain] = list;
                }

                list.Add(new ShardEntry(day, shardName, row.ObjectKey, row.RowCount));
            }

            foreach (var list in snapshot.Values)
            {
                list.Sort((a, b) => a.Day.CompareTo(b.Day));
            }

            var retainedKeys = snapshot.Values
                .SelectMany(entries => entries)
                .Select(entry => entry.ObjectKey)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var objectKey in _suppressedObjectKeys.Keys)
            {
                if (!retainedKeys.Contains(objectKey))
                {
                    _suppressedObjectKeys.TryRemove(objectKey, out _);
                }
            }

            _snapshot = snapshot;
            _loadedAt = DateTimeOffset.UtcNow;
            _logger.LogDebug("Cold ledger cache reloaded ({Domains} domains, {Rows} rows)", snapshot.Count, rows.Count);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private IReadOnlyList<ShardEntry> FilterSuppressed(List<ShardEntry> entries)
    {
        if (_suppressedObjectKeys.IsEmpty)
        {
            return entries;
        }

        return entries.Where(entry => !_suppressedObjectKeys.ContainsKey(entry.ObjectKey)).ToList();
    }

    private static string? ShardNameFromKey(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
        {
            return null;
        }

        var parts = objectKey.Replace('\\', '/').Split('/');
        var name = parts[^1];
        if (parts.Length > 1 && Guid.TryParseExact(parts[^2], "N", out _))
        {
            name = parts[^2] + "_" + name;
        }
        return name.EndsWith(".parquet", StringComparison.Ordinal) ? name[..^".parquet".Length] : null;
    }
}
