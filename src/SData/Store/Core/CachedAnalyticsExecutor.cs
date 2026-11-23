using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SData.Infra.Configuration;
using SData.Store.Models;

namespace SData.Store;

/// <summary>
/// 分析查询结果短 TTL 缓存装饰器：把窗口尾端对齐到 TTL 时间桶后缓存，
/// 仪表盘 {{logs}} 直查的重复轮询在 TTL 内直接命中；最坏数据陈旧度 = TTL。
/// 仅装饰 ExecuteSearchAsync（统一搜索直查）；告警计数与受限 ExecuteAsync 直通。
/// TTL ≤ 0 时直通内层执行器（缓存关闭）。
/// </summary>
public class CachedAnalyticsExecutor : IAnalyticsExecutor
{
    private const int CacheMaxRows = 1000;

    /// <summary>结果缓存 TTL（秒；压测择优，仪表盘轮询在 TTL 内命中）.</summary>
    private const int CacheTtlSeconds = 5;

    /// <summary>结果缓存最大条目数.</summary>
    private const int CacheMaxEntries = 256;

    private readonly IAnalyticsExecutor _inner;
    private readonly MemoryCache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachedAnalyticsExecutor"/> class.
    /// </summary>
    /// <param name="inner">内层执行器.</param>
    public CachedAnalyticsExecutor(IAnalyticsExecutor inner)
    {
        _inner = inner;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = CacheMaxEntries,
        });
    }

    /// <inheritdoc/>
    public Task<AlertCountResult> ExecuteAlertCountAsync(AlertCountQuery query, CancellationToken cancellationToken = default)
        => _inner.ExecuteAlertCountAsync(query, cancellationToken);

    /// <inheritdoc/>
    public Task<AnalyticsQueryResult> ExecuteAsync(string sql, int rowLimit = 1000, CancellationToken cancellationToken = default)
        => _inner.ExecuteAsync(sql, rowLimit, cancellationToken);

    /// <inheritdoc/>
    public async Task<AnalyticsQueryResult> ExecuteSearchAsync(string sql, DateTimeOffset from, DateTimeOffset to, int rowLimit = 500, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        var ttlSeconds = CacheTtlSeconds;
        if (ttlSeconds <= 0)
        {
            return await _inner.ExecuteSearchAsync(sql, from, to, rowLimit, parameters, cancellationToken);
        }

        var ttl = TimeSpan.FromSeconds(ttlSeconds);
        var duration = to - from;
        var now = DateTimeOffset.UtcNow;
        var flooredTo = new DateTimeOffset(now.UtcDateTime.Ticks - (now.UtcDateTime.Ticks % ttl.Ticks), TimeSpan.Zero);
        var normalizedFrom = flooredTo - duration;

        // 归一化后的窗口必须仍在原窗口之内或等于（只允许把 to 往前对齐），否则退回直通
        if (duration <= TimeSpan.Zero || normalizedFrom > from)
        {
            return await _inner.ExecuteSearchAsync(sql, from, to, rowLimit, parameters, cancellationToken);
        }

        var key = JsonSerializer.Serialize(new
        {
            sql,
            from = normalizedFrom,
            to = flooredTo,
            rowLimit,
            parameters,
        }, JsonOpts);
        if (_cache.TryGetValue(key, out AnalyticsQueryResult? cached) && cached != null)
        {
            return cached;
        }

        var result = await _inner.ExecuteSearchAsync(sql, normalizedFrom, flooredTo, rowLimit, parameters, cancellationToken);
        if (result.Rows.Count <= CacheMaxRows)
        {
            _cache.Set(key, result, new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = ttl,
            });
        }

        return result;
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.General);
}
