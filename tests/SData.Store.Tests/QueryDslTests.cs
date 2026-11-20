using global::Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Infra.Exceptions;
using SData.Store;
using SData.Store.Core;
using SData.Store.Models;
using SData.Store.QueryDsl;

namespace SData.Store.Tests;

/// <summary>
/// 统一查询 DSL 编译器测试（T310，契约 C1）：每域编译用例 + 白名单拒绝 + 参数化绑定 + 真实 DuckDB 执行.
/// </summary>
public class QueryDslTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 11, 15, 1, 0, 0, TimeSpan.Zero);

    private static QueryDslModel Base() => new() { Range = new QueryRange { From = From, To = To } };

    private static JsonElement El<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static object? Param(CompiledQuery query, string name)
    {
        return query.Parameters.First(p => p.ParameterName == name).Value;
    }

    private static BusinessException CompileThrows(QueryDslModel model, DataDomain domain)
    {
        var exception = Assert.Throws<BusinessException>(() => QueryDslCompiler.Compile(model, domain));
        Assert.Equal(400, exception.StatusCode);
        return exception;
    }

    // ---- logs 域 ----

    /// <summary>logs 原始行查询：eq/prefix 过滤、显式排序与 limit 参数化.</summary>
    [Fact]
    public void Logs_RawQuery_FiltersSortLimit()
    {
        var model = Base();
        model.Filters.Add(new QueryFilter { Field = "service", Op = "eq", Value = El("gateway") });
        model.Filters.Add(new QueryFilter { Field = "severity", Op = "prefix", Value = El("WARN") });
        model.Sort.Add(new QuerySort { Field = "body", Desc = false });
        model.Limit = 10;

        var query = QueryDslCompiler.Compile(model, DataDomain.Logs);

        Assert.Contains("logs_20261115", query.Sql, StringComparison.Ordinal);
        Assert.Contains("service = $p0", query.Sql, StringComparison.Ordinal);
        Assert.Contains("starts_with(severity, $p1)", query.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY body ASC", query.Sql, StringComparison.Ordinal);
        Assert.Equal("gateway", Param(query, "p0"));
        Assert.Equal("WARN", Param(query, "p1"));
        Assert.Equal(10, Param(query, "limit"));
        Assert.Single(query.Tables);
    }

    /// <summary>logs match：LIKE 通配转义 + % 包裹（防注入通配符）.</summary>
    [Fact]
    public void Logs_MatchBody_EscapesLikeWildcards()
    {
        var model = Base();
        model.Filters.Add(new QueryFilter { Field = "body", Op = "match", Value = El("disk%100_") });

        var query = QueryDslCompiler.Compile(model, DataDomain.Logs);

        Assert.Contains("body LIKE $p0", query.Sql, StringComparison.Ordinal);
        Assert.Equal("%disk\\%100\\_%", Param(query, "p0"));
    }

    /// <summary>logs IN 过滤：多值逐个参数绑定.</summary>
    [Fact]
    public void Logs_InFilter_Parametrized()
    {
        var model = Base();
        model.Filters.Add(new QueryFilter { Field = "trace_id", Op = "in", Value = El(new[] { "a1", "b2" }) });

        var query = QueryDslCompiler.Compile(model, DataDomain.Logs);

        Assert.Contains("trace_id IN ($p0, $p1)", query.Sql, StringComparison.Ordinal);
        Assert.Equal("a1", Param(query, "p0"));
        Assert.Equal("b2", Param(query, "p1"));
    }

    /// <summary>logs JSON 路径过滤：attributes.k8s.pod 走 json_extract_string.</summary>
    [Fact]
    public void Logs_JsonPathFilter_UsesJsonExtract()
    {
        var model = Base();
        model.Filters.Add(new QueryFilter { Field = "attributes.k8s.pod", Op = "eq", Value = El("web-1") });

        var query = QueryDslCompiler.Compile(model, DataDomain.Logs);

        Assert.Contains("json_extract_string(attributes, '$.\"k8s.pod\"') = $p0", query.Sql, StringComparison.Ordinal);
        Assert.Equal("web-1", Param(query, "p0"));
    }

    /// <summary>logs 分桶直方图：time_bucket + groupBy + count，按 bucket 升序.</summary>
    [Fact]
    public void Logs_BucketHistogram_GroupByService()
    {
        var model = Base();
        model.Bucket = new QueryBucket { Step = "5m", Func = "count" };
        model.GroupBy.Add("service");

        var query = QueryDslCompiler.Compile(model, DataDomain.Logs);

        Assert.Contains("time_bucket(INTERVAL '5 minutes', ts) AS \"bucket\"", query.Sql, StringComparison.Ordinal);
        Assert.Contains("service AS \"service\"", query.Sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(*) AS \"count\"", query.Sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY \"bucket\", \"service\"", query.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"bucket\"", query.Sql, StringComparison.Ordinal);
    }

    /// <summary>logs 白名单外字段/非法 JSON 路径拒绝（400）.</summary>
    [Fact]
    public void Logs_RejectsUnknownFieldAndUnsafeJsonPath()
    {
        var bad1 = Base();
        bad1.Filters.Add(new QueryFilter { Field = "secret_col", Op = "eq", Value = El("x") });
        CompileThrows(bad1, DataDomain.Logs);

        var bad2 = Base();
        bad2.Filters.Add(new QueryFilter { Field = "attributes.pod;drop", Op = "eq", Value = El("x") });
        CompileThrows(bad2, DataDomain.Logs);
    }

    // ---- traces 域 ----

    /// <summary>traces 默认原始行查询：全列投影 + 默认排序与 limit.</summary>
    [Fact]
    public void Traces_DefaultRawQuery()
    {
        var query = QueryDslCompiler.Compile(Base(), DataDomain.Traces);

        Assert.Contains("SELECT trace_id, span_id, parent_span_id, ts, duration_us", query.Sql, StringComparison.Ordinal);
        Assert.Contains("traces_20261115", query.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY ts DESC", query.Sql, StringComparison.Ordinal);
        Assert.Equal(50, Param(query, "limit"));
    }

    /// <summary>traces 数值字段过滤绑定 double.</summary>
    [Fact]
    public void Traces_DurationEq_BindsNumber()
    {
        var model = Base();
        model.Filters.Add(new QueryFilter { Field = "duration_us", Op = "neq", Value = El(5000) });

        var query = QueryDslCompiler.Compile(model, DataDomain.Traces);

        Assert.Contains("duration_us != $p0", query.Sql, StringComparison.Ordinal);
        Assert.Equal(5000.0, Param(query, "p0"));
    }

    /// <summary>traces JSON 属性 IN 过滤.</summary>
    [Fact]
    public void Traces_JsonAttrIn()
    {
        var model = Base();
        model.Filters.Add(new QueryFilter { Field = "attributes.http.method", Op = "in", Value = El(new[] { "GET", "POST" }) });

        var query = QueryDslCompiler.Compile(model, DataDomain.Traces);

        Assert.Contains("json_extract_string(attributes, '$.\"http.method\"') IN ($p0, $p1)", query.Sql, StringComparison.Ordinal);
    }

    /// <summary>traces 聚合：quantile_cont p75 + groupBy service + 按别名降序.</summary>
    [Fact]
    public void Traces_QuantileGroupBy_SortByAlias()
    {
        var model = Base();
        model.Aggregations.Add(new QueryAggregation { Func = "quantile", Field = "duration_us", Q = 0.75, Alias = "p75" });
        model.GroupBy.Add("service");
        model.Sort.Add(new QuerySort { Field = "p75", Desc = true });

        var query = QueryDslCompiler.Compile(model, DataDomain.Traces);

        Assert.Contains("quantile_cont(duration_us, $q", query.Sql, StringComparison.Ordinal);
        Assert.Contains(" AS \"p75\"", query.Sql, StringComparison.Ordinal);
        Assert.Contains("service AS \"service\"", query.Sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY \"service\"", query.Sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"p75\" DESC", query.Sql, StringComparison.Ordinal);
        Assert.Equal(0.75, Param(query, query.Parameters.Last(p => p.ParameterName.StartsWith("q", StringComparison.Ordinal)).ParameterName));
    }

    /// <summary>traces 聚合拒绝：avg 作用于时间字段、排序引用未知别名、非法 op.</summary>
    [Fact]
    public void Traces_RejectsBadAggFieldSortAliasAndOp()
    {
        var bad1 = Base();
        bad1.Aggregations.Add(new QueryAggregation { Func = "avg", Field = "ts", Alias = "t" });
        CompileThrows(bad1, DataDomain.Traces);

        var bad2 = Base();
        bad2.Aggregations.Add(new QueryAggregation { Func = "count", Alias = "total" });
        bad2.Sort.Add(new QuerySort { Field = "nope" });
        CompileThrows(bad2, DataDomain.Traces);

        var bad3 = Base();
        bad3.Filters.Add(new QueryFilter { Field = "service", Op = "regex", Value = El("x") });
        CompileThrows(bad3, DataDomain.Traces);
    }

    /// <summary>traces 跨天查询：表集合按 UTC 天展开为多分支 UNION.</summary>
    [Fact]
    public void Traces_MultiDayRange_ExpandsDayTables()
    {
        var model = Base();
        model.Range = new QueryRange { From = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero), To = new DateTimeOffset(2026, 11, 16, 6, 0, 0, TimeSpan.Zero) };

        var query = QueryDslCompiler.Compile(model, DataDomain.Traces);

        Assert.Equal(3, query.Tables.Count);
        Assert.Equal(new[] { "traces_20261114", "traces_20261115", "traces_20261116" }, query.Tables);
        Assert.Contains(" UNION ALL ", query.Sql, StringComparison.Ordinal);
    }

    // ---- 比较操作符（gt/gte/lt/lte）----

    /// <summary>traces 数值字段 gt：duration_us > 绑定 double.</summary>
    [Fact]
    public void Traces_DurationGt_BindsNumber()
    {
        var model = Base();
        model.Filters.Add(new QueryFilter { Field = "duration_us", Op = "gt", Value = El(100000) });
        model.Filters.Add(new QueryFilter { Field = "duration_us", Op = "lte", Value = El(500000.5) });

        var query = QueryDslCompiler.Compile(model, DataDomain.Traces);

        Assert.Contains("duration_us > $p0", query.Sql, StringComparison.Ordinal);
        Assert.Contains("duration_us <= $p1", query.Sql, StringComparison.Ordinal);
        Assert.Equal(100000.0, Param(query, "p0"));
        Assert.Equal(500000.5, Param(query, "p1"));
    }

    /// <summary>logs JSON 字段 gte：TRY_CAST 数值比较 + 数值绑定.</summary>
    [Fact]
    public void Logs_JsonGte_UsesTryCastAndNumberBind()
    {
        var model = Base();
        model.Filters.Add(new QueryFilter { Field = "attributes.status_code", Op = "gte", Value = El(400) });

        var query = QueryDslCompiler.Compile(model, DataDomain.Logs);

        Assert.Contains("TRY_CAST(json_extract_string(attributes, '$.\"status_code\"') AS DOUBLE) >= $p0", query.Sql, StringComparison.Ordinal);
        Assert.Equal(400.0, Param(query, "p0"));
    }

    /// <summary>字符串字段比较拒绝（400）.</summary>
    [Fact]
    public void Logs_StringFieldComparison_Rejected()
    {
        var bad = Base();
        bad.Filters.Add(new QueryFilter { Field = "service", Op = "lt", Value = El("abc") });
        CompileThrows(bad, DataDomain.Logs);
    }

    /// <summary>时间字段 lt：ISO 8601 绑定 UTC DateTime.</summary>
    [Fact]
    public void Traces_TimeFieldLt_BindsUtcDateTime()
    {
        var model = Base();
        model.Filters.Add(new QueryFilter { Field = "ts", Op = "lt", Value = El("2026-11-15T00:30:00Z") });

        var query = QueryDslCompiler.Compile(model, DataDomain.Traces);

        Assert.Contains("ts < $p0", query.Sql, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 11, 15, 0, 30, 0, DateTimeKind.Utc), Param(query, "p0"));
    }

    // ---- metrics 域 ----

    /// <summary>metrics 分桶 avg：1h 步长 + 别名输出.</summary>
    [Fact]
    public void Metrics_BucketAvgValue()
    {
        var model = Base();
        model.Bucket = new QueryBucket { Step = "1h", Func = "avg", Field = "value" };

        var query = QueryDslCompiler.Compile(model, DataDomain.Metrics);

        Assert.Contains("time_bucket(INTERVAL '1 hour', ts) AS \"bucket\"", query.Sql, StringComparison.Ordinal);
        Assert.Contains("AVG(value) AS \"avg\"", query.Sql, StringComparison.Ordinal);
    }

    /// <summary>metrics labels JSON 过滤（json_extract_string 标签过滤）.</summary>
    [Fact]
    public void Metrics_LabelEq_UsesJsonExtract()
    {
        var model = Base();
        model.Filters.Add(new QueryFilter { Field = "labels.host", Op = "eq", Value = El("n1") });

        var query = QueryDslCompiler.Compile(model, DataDomain.Metrics);

        Assert.Contains("json_extract_string(labels, '$.\"host\"') = $p0", query.Sql, StringComparison.Ordinal);
    }

    /// <summary>metrics quantile 默认 q=0.5.</summary>
    [Fact]
    public void Metrics_Quantile_DefaultQ()
    {
        var model = Base();
        model.Aggregations.Add(new QueryAggregation { Func = "quantile", Field = "value" });

        var query = QueryDslCompiler.Compile(model, DataDomain.Metrics);

        Assert.Contains("quantile_cont(value, $q2) AS \"quantile\"", query.Sql, StringComparison.Ordinal);
        Assert.Equal(0.5, Param(query, "q2"));
    }

    /// <summary>metrics 值数组 IN：数字数组绑定 double.</summary>
    [Fact]
    public void Metrics_ValueIn_BindsNumbers()
    {
        var model = Base();
        model.Filters.Add(new QueryFilter { Field = "value", Op = "in", Value = El(new[] { 1.5, 2.5 }) });

        var query = QueryDslCompiler.Compile(model, DataDomain.Metrics);

        Assert.Contains("value IN ($p0, $p1)", query.Sql, StringComparison.Ordinal);
        Assert.Equal(1.5, Param(query, "p0"));
        Assert.Equal(2.5, Param(query, "p1"));
    }

    /// <summary>metrics 聚合 groupBy name：GROUP BY 别名引用.</summary>
    [Fact]
    public void Metrics_GroupByName_WithCount()
    {
        var model = Base();
        model.GroupBy.Add("name");
        model.Aggregations.Add(new QueryAggregation { Func = "count", Alias = "total" });

        var query = QueryDslCompiler.Compile(model, DataDomain.Metrics);

        Assert.Contains("name AS \"name\"", query.Sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(*) AS \"total\"", query.Sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY \"name\"", query.Sql, StringComparison.Ordinal);
    }

    /// <summary>metrics 拒绝：非法步长、非法聚合函数、limit 越界收敛到 10000.</summary>
    [Fact]
    public void Metrics_RejectsBadStepFunc_AndClampsLimit()
    {
        var bad1 = Base();
        bad1.Bucket = new QueryBucket { Step = "5x" };
        CompileThrows(bad1, DataDomain.Metrics);

        var bad2 = Base();
        bad2.Aggregations.Add(new QueryAggregation { Func = "median", Field = "value" });
        CompileThrows(bad2, DataDomain.Metrics);

        var clamped = Base();
        clamped.Limit = 999999;
        var query = QueryDslCompiler.Compile(clamped, DataDomain.Metrics);
        Assert.Equal(10000, Param(query, "limit"));
    }

    // ---- iot 域 ----

    /// <summary>iot 设备前缀过滤 + 字符串 match.</summary>
    [Fact]
    public void Iot_PrefixAndMatch()
    {
        var model = Base();
        model.Filters.Add(new QueryFilter { Field = "device_key", Op = "prefix", Value = El("dev-") });
        model.Filters.Add(new QueryFilter { Field = "str_value", Op = "match", Value = El("正常") });

        var query = QueryDslCompiler.Compile(model, DataDomain.Iot);

        Assert.Contains("starts_with(device_key, $p0)", query.Sql, StringComparison.Ordinal);
        Assert.Contains("str_value LIKE $p1", query.Sql, StringComparison.Ordinal);
        Assert.Equal("%正常%", Param(query, "p1"));
    }

    /// <summary>iot 数值过滤绑定 double.</summary>
    [Fact]
    public void Iot_NumValueEq()
    {
        var model = Base();
        model.Filters.Add(new QueryFilter { Field = "num_value", Op = "eq", Value = El(22.5) });

        var query = QueryDslCompiler.Compile(model, DataDomain.Iot);

        Assert.Contains("num_value = $p0", query.Sql, StringComparison.Ordinal);
        Assert.Equal(22.5, Param(query, "p0"));
    }

    /// <summary>iot 聚合：groupBy signal + sum.</summary>
    [Fact]
    public void Iot_GroupBySignal_Sum()
    {
        var model = Base();
        model.GroupBy.Add("signal");
        model.Aggregations.Add(new QueryAggregation { Func = "sum", Field = "num_value", Alias = "total" });

        var query = QueryDslCompiler.Compile(model, DataDomain.Iot);

        Assert.Contains("signal AS \"signal\"", query.Sql, StringComparison.Ordinal);
        Assert.Contains("SUM(num_value) AS \"total\"", query.Sql, StringComparison.Ordinal);
    }

    /// <summary>iot 拒绝：无 labels 列的 JSON 前缀、groupBy 无聚合.</summary>
    [Fact]
    public void Iot_RejectsWrongJsonHeadAndGroupByWithoutAgg()
    {
        var bad1 = Base();
        bad1.Filters.Add(new QueryFilter { Field = "labels.host", Op = "eq", Value = El("n1") });
        CompileThrows(bad1, DataDomain.Iot);

        var bad2 = Base();
        bad2.GroupBy.Add("signal");
        CompileThrows(bad2, DataDomain.Iot);
    }

    /// <summary>iot 拒绝：缺少 range、from>=to.</summary>
    [Fact]
    public void Iot_RejectsBadRange()
    {
        var bad1 = new QueryDslModel { Filters = [new QueryFilter { Field = "signal", Op = "eq", Value = El("temp") }] };
        CompileThrows(bad1, DataDomain.Iot);

        var bad2 = Base();
        bad2.Range = new QueryRange { From = To, To = From };
        CompileThrows(bad2, DataDomain.Iot);
    }

    /// <summary>iot 聚合 min/max 允许时间字段.</summary>
    [Fact]
    public void Iot_MinTs_Allowed()
    {
        var model = Base();
        model.Aggregations.Add(new QueryAggregation { Func = "min", Field = "ts", Alias = "first_seen" });

        var query = QueryDslCompiler.Compile(model, DataDomain.Iot);

        Assert.Contains("MIN(ts) AS \"first_seen\"", query.Sql, StringComparison.Ordinal);
    }

    // ---- 真实 DuckDB 执行 ----

    /// <summary>logs 分组计数端到端：缓冲落库后按 DSL 聚合查询.</summary>
    [Fact]
    public async Task Execute_LogsGroupCount()
    {
        using var harness = new DslHarness();
        var now = DateTimeOffset.UtcNow;
        var batch = new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now, Service = "svc1", Severity = "INFO", Body = "a" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now, Service = "svc1", Severity = "WARN", Body = "b" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now, Service = "svc2", Severity = "INFO", Body = "c" }),
        };
        await harness.Writer.FlushAsync(batch, harness.Token);

        var model = new QueryDslModel
        {
            Range = new QueryRange { From = now.AddMinutes(-5), To = now.AddMinutes(5) },
            Filters = [new QueryFilter { Field = "service", Op = "prefix", Value = El("svc") }],
            GroupBy = ["service"],
            Aggregations = [new QueryAggregation { Func = "count", Alias = "total" }],
            Sort = [new QuerySort { Field = "total", Desc = true }],
            Limit = 10,
        };
        var query = QueryDslCompiler.Compile(model, DataDomain.Logs);
        var rows = await harness.Manager.QueryAsync(query.Sql, query.Parameters, async reader =>
        {
            var list = new List<(string Service, long Total)>();
            while (await reader.ReadAsync(harness.Token))
            {
                list.Add((reader.GetString(0), reader.GetInt64(1)));
            }

            return list;
        }, harness.Token);

        Assert.Equal(2, rows.Count);
        Assert.Equal(("svc1", 2L), rows[0]);
        Assert.Equal(("svc2", 1L), rows[1]);
    }

    /// <summary>metrics 分桶均值端到端：同小时两点 avg 入一个桶.</summary>
    [Fact]
    public async Task Execute_MetricsBucketAvg()
    {
        using var harness = new DslHarness();
        var now = DateTimeOffset.UtcNow;
        var batch = new List<IngestItem>
        {
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = now.AddMinutes(-1), Value = 10 }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = now.AddMinutes(-2), Value = 20 }),
        };
        await harness.Writer.FlushAsync(batch, harness.Token);

        var model = new QueryDslModel
        {
            Range = new QueryRange { From = now.AddMinutes(-5), To = now.AddMinutes(5) },
            Filters = [new QueryFilter { Field = "name", Op = "eq", Value = El("cpu") }],
            Bucket = new QueryBucket { Step = "1h", Func = "avg", Field = "value" },
            Limit = 10,
        };
        var query = QueryDslCompiler.Compile(model, DataDomain.Metrics);
        var rows = await harness.Manager.QueryAsync(query.Sql, query.Parameters, async reader =>
        {
            var list = new List<double>();
            while (await reader.ReadAsync(harness.Token))
            {
                list.Add(reader.GetDouble(1));
            }

            return list;
        }, harness.Token);

        var average = Assert.Single(rows);
        Assert.Equal(15.0, average, 6);
    }

    /// <summary>metrics 数值比较端到端：value > 15 只命中较大点.</summary>
    [Fact]
    public async Task Execute_MetricsValueGt()
    {
        using var harness = new DslHarness();
        var now = DateTimeOffset.UtcNow;
        var batch = new List<IngestItem>
        {
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = now.AddMinutes(-1), Value = 10 }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = now.AddMinutes(-2), Value = 20 }),
        };
        await harness.Writer.FlushAsync(batch, harness.Token);

        var model = new QueryDslModel
        {
            Range = new QueryRange { From = now.AddMinutes(-5), To = now.AddMinutes(5) },
            Filters = [new QueryFilter { Field = "name", Op = "eq", Value = El("cpu") }, new QueryFilter { Field = "value", Op = "gt", Value = El(15) }],
            Limit = 10,
        };
        var query = QueryDslCompiler.Compile(model, DataDomain.Metrics);
        var rows = await harness.Manager.QueryAsync(query.Sql, query.Parameters, async reader =>
        {
            var list = new List<double>();
            while (await reader.ReadAsync(harness.Token))
            {
                list.Add(reader.GetDouble(2));
            }

            return list;
        }, harness.Token);

        var value = Assert.Single(rows);
        Assert.Equal(20.0, value, 6);
    }

    /// <summary>DuckDB 执行测试夹具：临时库 + 缓冲 + 写入器.</summary>
    private sealed class DslHarness : IDisposable
    {
        /// <summary>临时目录.</summary>
        public readonly string TempDirectory;

        /// <summary>系统配置.</summary>
        public readonly SystemOptions Options;

        /// <summary>连接管理器.</summary>
        public readonly DuckDbConnectionManager Manager;

        /// <summary>接入缓冲.</summary>
        public readonly IngestBuffer Buffer;

        /// <summary>批量写入器.</summary>
        public readonly IngestWriterService Writer;

        /// <summary>取消令牌.</summary>
        public readonly CancellationToken Token = CancellationToken.None;

        /// <summary>
        /// Initializes a new instance of the <see cref="DslHarness"/> class.
        /// </summary>
        public DslHarness()
        {
            TempDirectory = Path.Combine(Path.GetTempPath(), "sdata-dsl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDirectory);

            Options = new SystemOptions();
            Options.DuckDb.Path = Path.Combine(TempDirectory, "dsl-test.duckdb");
            Options.DuckDb.Threads = 2;

            Manager = new DuckDbConnectionManager(Options, NullLogger<DuckDbConnectionManager>.Instance);
            Manager.InitializeAsync().GetAwaiter().GetResult();
            Buffer = new IngestBuffer(Options);
            Writer = new IngestWriterService(Buffer, Manager, Options, new SData.Infra.Services.DefaultIdProvider(1), NullLogger<IngestWriterService>.Instance);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不阻塞测试
            }
        }
    }
}
