using global::Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Metrics.Promql;
using SData.Store;
using SData.Store.Core;
using SData.Store.QueryDsl;

namespace SData.Metrics.Tests;

/// <summary>
/// PromQL 端到端测试（T711）：DuckDB 写入 metrics 天表 → AST→SQL → Prometheus 形状序列.
/// </summary>
public class PromqlExecutorTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly PromqlExecutor _executor;

    /// <summary>
    /// Initializes a new instance of the <see cref="PromqlExecutorTests"/> class.
    /// </summary>
    public PromqlExecutorTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "sdata-prom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        AppConst.ContentRoot = _tempDirectory;

        var options = new SystemOptions();
        options.DuckDb.Path = "prom-test.duckdb";
        options.DuckDb.Threads = 2;
        options.S3 = new S3Options();

        _connectionManager = new DuckDbConnectionManager(options, NullLogger<DuckDbConnectionManager>.Instance);
        _connectionManager.InitializeAsync().GetAwaiter().GetResult();
        var today = DateTimeOffset.UtcNow;
        _connectionManager.EnsureDayTablesAsync(TimeTables.MetricsPrefix, [today, today.AddDays(1)]).GetAwaiter().GetResult();

        var resolver = new LocalOnlyResolver(_connectionManager);
        _executor = new PromqlExecutor(_connectionManager, resolver, options);
    }

    /// <summary>
    /// rate(counter[5m])：三样本 (100,200,400) → (max-min)/300s ≈ 1.0/s（任务卡验收表达式）.
    /// </summary>
    [Fact]
    public async Task Rate_EndToEnd_ReturnsPerSecondDelta()
    {
        var now = DateTimeOffset.UtcNow;
        Insert("http_server_requests_total", now.AddSeconds(-300), 100, "{\"service\":\"api\"}");
        Insert("http_server_requests_total", now.AddSeconds(-150), 200, "{\"service\":\"api\"}");
        Insert("http_server_requests_total", now.AddSeconds(-1), 400, "{\"service\":\"api\"}");

        var from = now.AddHours(-1);
        var to = now.AddMinutes(1);
        var result = await _executor.ExecuteAsync("rate(http_server_requests_total[5m])", from, to, null);

        Assert.Equal("vector", result.ResultType);
        var series = Assert.Single(result.Result);
        Assert.Equal("http_server_requests_total", series.Metric["__name__"]);
        Assert.Equal("api", series.Metric["service"]);
        Assert.Equal(1.0, series.Value!.Value.Value, precision: 1); // (400-100)/300 ≈ 1
    }

    /// <summary>
    /// sum by (service)：两序列同 service 求和合并为单序列.
    /// </summary>
    [Fact]
    public async Task SumBy_GroupsByLabel()
    {
        var now = DateTimeOffset.UtcNow;
        Insert("cpu_usage", now, 10, "{\"service\":\"a\",\"host\":\"h1\"}");
        Insert("cpu_usage", now, 20, "{\"service\":\"a\",\"host\":\"h2\"}");
        Insert("cpu_usage", now, 5, "{\"service\":\"b\",\"host\":\"h1\"}");

        var result = await _executor.ExecuteAsync("sum by (service) (cpu_usage)", now.AddMinutes(-5), now.AddMinutes(5), null);

        Assert.Equal(2, result.Result.Count);
        var a = result.Result.Single(x => x.Metric.GetValueOrDefault("service") == "a");
        Assert.Equal(30, a.Value!.Value.Value);
        Assert.False(a.Metric.ContainsKey("__name__")); // 聚合后无指标名
        var b = result.Result.Single(x => x.Metric.GetValueOrDefault("service") == "b");
        Assert.Equal(5, b.Value!.Value.Value);
    }

    /// <summary>
    /// topk 排序截断与 query_range matrix 形状.
    /// </summary>
    [Fact]
    public async Task Topk_And_RangeQuery()
    {
        var now = DateTimeOffset.UtcNow;
        Insert("mem_used", now, 1, "{\"host\":\"h1\"}");
        Insert("mem_used", now, 9, "{\"host\":\"h2\"}");
        Insert("mem_used", now, 5, "{\"host\":\"h3\"}");

        var result = await _executor.ExecuteAsync("topk(2, mem_used)", now.AddMinutes(-1), now.AddMinutes(1), null);
        Assert.Equal(2, result.Result.Count);
        Assert.Equal(9, result.Result[0].Value!.Value.Value);
        Assert.Equal(5, result.Result[1].Value!.Value.Value);

        var range = await _executor.ExecuteAsync("mem_used", now.AddSeconds(-30), now.AddSeconds(30), TimeSpan.FromSeconds(10));
        Assert.Equal("matrix", range.ResultType);
        Assert.Equal(3, range.Result.Count); // 每序列一个 series
        var matrix = range.Result.Single(x => x.Metric.GetValueOrDefault("host") == "h2");
        Assert.Equal(1, matrix.Values.Count); // 数据仅 now 一个时间点，只落一个桶
        Assert.Equal(9, matrix.Values.Last().Value);
    }

    /// <summary>
    /// 非法表达式 → PromqlParseException（Controller 转 Prometheus 风格 error）.
    /// </summary>
    [Fact]
    public async Task InvalidExpression_ThrowsParseError()
    {
        await Assert.ThrowsAsync<PromqlParseException>(
            () => _executor.ExecuteAsync("sum(", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
    }

    private void Insert(string name, DateTimeOffset ts, double value, string labels)
    {
        var today = TimeTables.Name(TimeTables.MetricsPrefix, ts);
        var sql = $"INSERT INTO {today} (name, ts, value, labels, kind) VALUES ($name, $ts, $value, $labels, 'gauge')";
        _connectionManager.ExecuteWriteAsync(sql,
        [
            new DuckDB.NET.Data.DuckDBParameter("name", name),
            new DuckDB.NET.Data.DuckDBParameter("ts", ts.ToUniversalTime().UtcDateTime),
            new DuckDB.NET.Data.DuckDBParameter("value", value),
            new DuckDB.NET.Data.DuckDBParameter("labels", labels),
        ]).GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            _connectionManager.DisposeAsync().GetAwaiter().GetResult();
        }
        finally
        {
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>仅本地天表的桩解析器（PromQL 语义测试不依赖存储基础设施；无冷层、无台账）.</summary>
    private sealed class LocalOnlyResolver(DuckDbConnectionManager manager) : ITableResolver
    {
        public Task<ResolvedTables> ResolveAsync(DataDomain domain, DateTimeOffset from, DateTimeOffset to, string? searchKeyword = null, CancellationToken cancellationToken = default)
        {
            var tables = manager.GetDayTables(TimeTables.MetricsPrefix, from, to);
            return Task.FromResult(new ResolvedTables(tables, Array.Empty<string>()));
        }
    }
}
