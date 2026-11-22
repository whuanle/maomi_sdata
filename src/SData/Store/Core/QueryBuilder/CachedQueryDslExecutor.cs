using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SData.Infra.Configuration;
using SData.Store.Models;
using SData.Store.QueryDsl;

namespace SData.Store;

/// <summary>
/// 查询结果短 TTL 缓存装饰器：把 range 尾端对齐到 TTL 时间桶后缓存，
/// 仪表盘/看板的重复轮询在 TTL 内直接命中；最坏数据陈旧度 = TTL。
/// TTL ≤ 0 时直通内层执行器（缓存关闭）。
/// </summary>
public class CachedQueryDslExecutor : IQueryDslExecutor
{
    private const int CacheMaxRows = 1000;

    private readonly IQueryDslExecutor _inner;
    private readonly SystemOptions _options;
    private readonly MemoryCache _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachedQueryDslExecutor"/> class.
    /// </summary>
    /// <param name="inner">内层执行器.</param>
    /// <param name="options">系统配置.</param>
    public CachedQueryDslExecutor(IQueryDslExecutor inner, SystemOptions options)
    {
        _inner = inner;
        _options = options;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = Math.Max(16, options.DuckDb.QueryCacheMaxEntries),
        });
    }

    /// <inheritdoc/>
    public async Task<SearchDslResponse> ExecuteAsync(QueryDslModel model, DataDomain domain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var ttlSeconds = _options.DuckDb.QueryCacheTtlSeconds;
        if (ttlSeconds <= 0 || model.Range == null)
        {
            return await _inner.ExecuteAsync(model, domain, cancellationToken);
        }

        var ttl = TimeSpan.FromSeconds(ttlSeconds);
        var duration = model.Range.To - model.Range.From;
        var now = DateTimeOffset.UtcNow;
        var flooredTo = new DateTimeOffset(now.UtcDateTime.Ticks - (now.UtcDateTime.Ticks % ttl.Ticks), TimeSpan.Zero);
        var normalizedFrom = flooredTo - duration;

        // 归一化后的窗口必须仍在原窗口之内或等于（只允许把 to 往前对齐），否则退回直通
        if (duration <= TimeSpan.Zero || normalizedFrom > model.Range.From)
        {
            return await _inner.ExecuteAsync(model, domain, cancellationToken);
        }

        var cachedModel = CloneWithRange(model, normalizedFrom, flooredTo);
        var key = JsonSerializer.Serialize(new { domain, model = cachedModel }, JsonOpts);
        if (_cache.TryGetValue(key, out SearchDslResponse? cached) && cached != null)
        {
            return cached;
        }

        var response = await _inner.ExecuteAsync(cachedModel, domain, cancellationToken);
        if (TotalRows(response) <= CacheMaxRows)
        {
            _cache.Set(key, response, new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = ttl,
            });
        }

        return response;
    }

    private static long TotalRows(SearchDslResponse response) => response.Rows.Count + (response.RowsArrays?.Count ?? 0);

    private static QueryDslModel CloneWithRange(QueryDslModel model, DateTimeOffset from, DateTimeOffset to)
    {
        return new QueryDslModel
        {
            Range = new QueryRange { From = from, To = to },
            Filters = model.Filters,
            GroupBy = model.GroupBy,
            Aggregations = model.Aggregations,
            Bucket = model.Bucket,
            Sort = model.Sort,
            Limit = model.Limit,
            Columnar = model.Columnar,
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.General);
}
