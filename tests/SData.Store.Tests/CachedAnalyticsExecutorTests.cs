using global::Xunit;
using SData.Infra.Configuration;
using SData.Store;
using SData.Store.Models;

namespace SData.Store.Tests;

/// <summary>
/// 分析查询结果 TTL 缓存测试：窗口尾端对齐后命中、TTL=0 直通、告警计数直通.
/// </summary>
public class CachedAnalyticsExecutorTests
{
    private static SystemOptions Options(int ttlSeconds) => new()
    {
        DuckDb = { AnalyticsQueryCacheTtlSeconds = ttlSeconds, AnalyticsQueryCacheMaxEntries = 16 },
    };

    private sealed class CountingExecutor : IAnalyticsExecutor
    {
        public int SearchCalls { get; private set; }

        public Task<AlertCountResult> ExecuteAlertCountAsync(AlertCountQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new AlertCountResult(7, false));

        public Task<AnalyticsQueryResult> ExecuteAsync(string sql, int rowLimit = 1000, CancellationToken cancellationToken = default)
            => Task.FromResult(new AnalyticsQueryResult());

        public Task<AnalyticsQueryResult> ExecuteSearchAsync(string sql, DateTimeOffset from, DateTimeOffset to, int rowLimit = 500, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            return Task.FromResult(new AnalyticsQueryResult { ElapsedMs = 1 });
        }
    }

    [Fact]
    public async Task SameTtlWindow_HitsCache()
    {
        var inner = new CountingExecutor();
        var cached = new CachedAnalyticsExecutor(inner, Options(30));
        // to 略超前于当前时间（仪表盘"最近 5 分钟"轮询形态）：尾端可向前对齐，同桶重复轮询命中
        var to = DateTimeOffset.UtcNow.AddSeconds(5);
        var from = to.AddMinutes(-5);

        var first = await cached.ExecuteSearchAsync("SELECT count(*) FROM {{logs}}", from, to);
        var second = await cached.ExecuteSearchAsync("SELECT count(*) FROM {{logs}}", from, to);

        Assert.Equal(1, inner.SearchCalls);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task DifferentWindow_ExecutesAgain()
    {
        var inner = new CountingExecutor();
        var cached = new CachedAnalyticsExecutor(inner, Options(30));
        var to = DateTimeOffset.UtcNow.AddSeconds(5);

        await cached.ExecuteSearchAsync("SELECT count(*) FROM {{logs}}", to.AddMinutes(-5), to);
        await cached.ExecuteSearchAsync("SELECT count(*) FROM {{logs}}", to.AddMinutes(-9), to);

        Assert.Equal(2, inner.SearchCalls);
    }

    [Fact]
    public async Task PastTo_PassesThrough()
    {
        var inner = new CountingExecutor();
        var cached = new CachedAnalyticsExecutor(inner, Options(30));
        // to 已是过去时刻：向前对齐会把窗口起点推后（normalizedFrom > from），按设计直通
        var to = DateTimeOffset.UtcNow.AddSeconds(-30);
        var from = to.AddMinutes(-5);

        await cached.ExecuteSearchAsync("SELECT 1", from, to);
        await cached.ExecuteSearchAsync("SELECT 1", from, to);

        Assert.Equal(2, inner.SearchCalls);
    }

    [Fact]
    public async Task TtlZero_PassesThrough()
    {
        var inner = new CountingExecutor();
        var cached = new CachedAnalyticsExecutor(inner, Options(0));
        var to = DateTimeOffset.UtcNow.AddSeconds(5);

        await cached.ExecuteSearchAsync("SELECT 1", to.AddMinutes(-5), to);
        await cached.ExecuteSearchAsync("SELECT 1", to.AddMinutes(-5), to);

        Assert.Equal(2, inner.SearchCalls);
    }

    [Fact]
    public async Task AlertCount_BypassesCache()
    {
        var inner = new CountingExecutor();
        var cached = new CachedAnalyticsExecutor(inner, Options(30));
        var query = new AlertCountQuery("logs", "level = 'ERROR'", new Dictionary<string, object?>(), DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, 100);

        var result = await cached.ExecuteAlertCountAsync(query);

        Assert.Equal(7, result.Count);
        Assert.Equal(0, inner.SearchCalls);
    }
}
