using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Dashboard;
using SData.Database;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Metrics;
using SData.Metrics.Promql;
using SData.Settings.Services;
using SData.Store;
using SData.Store.Core;
using SData.Store.QueryDsl;

namespace SData.Dashboard.Tests;

/// <summary>
/// 面板查询执行器端到端测试：DuckDB 写入真实天表 → 按 kind 分派（metric/dsl/promql/sql）→ 面板数据.
/// </summary>
public class PanelQueryExecutorTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly PanelQueryExecutor _executor;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelQueryExecutorTests"/> class.
    /// </summary>
    public PanelQueryExecutorTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "sdata-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        AppConst.ContentRoot = _tempDirectory;

        var options = new SystemOptions();
        options.DuckDb.Path = "panel-test.duckdb";
        options.DuckDb.Threads = 2;
        options.S3 = new S3Options();

        _connectionManager = new DuckDbConnectionManager(options, NullLogger<DuckDbConnectionManager>.Instance);
        _connectionManager.InitializeAsync().GetAwaiter().GetResult();
        // 覆盖跨午夜：前一天 / 当天 / 明天的天表都建好
        var days = new[] { _now.AddDays(-1), _now, _now.AddDays(1) };
        _connectionManager.EnsureDayTablesAsync(TimeTables.MetricsPrefix, days).GetAwaiter().GetResult();
        _connectionManager.EnsureDayTablesAsync(TimeTables.LogsPrefix, days).GetAwaiter().GetResult();

        var resolver = new LocalOnlyResolver(_connectionManager);
        var databaseContext = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite($"Data Source={Path.Combine(_tempDirectory, "meta.db")}")
            .Options);
        databaseContext.Database.EnsureCreated();

        _executor = new PanelQueryExecutor(
            new MetricStore(_connectionManager, options),
            new QueryDslExecutorService(_connectionManager, SystemSettingsProvider.FileOnly(options), resolver),
            new AnalyticsExecutor(_connectionManager, options, SystemSettingsProvider.FileOnly(options), resolver, NullLogger<AnalyticsExecutor>.Instance),
            new PromqlExecutor(_connectionManager, resolver, SystemSettingsProvider.FileOnly(options)),
            databaseContext,
            NullLogger<PanelQueryExecutor>.Instance);
    }

    [Fact]
    public async Task CustomPanel_BindsIndependentInputsAndEvaluatesDependencies()
    {
        var definition = new CustomPanelDefinition
        {
            Nodes =
            [
                new() { Ref = "A", Sql = "SELECT $value AS value", Parameters = new() { ["value"] = 20 } },
                new() { Ref = "B", Sql = "SELECT $value AS value", Parameters = new() { ["value"] = 5 } },
                new() { Ref = "C", Kind = "expression", Expression = "A / B * 100" },
                new() { Ref = "D", Kind = "expression", Expression = "C / 0" },
            ],
            Outputs = ["C", "D"],
        };
        var data = await _executor.ExecuteAsync(new PanelConfig
        {
            Id = "custom", Type = "stat",
            Query = new() { Kind = PanelQueryKinds.Custom, Custom = definition },
        }, _now.AddHours(-1), _now);
        Assert.Null(data.Error);
        Assert.Equal(400d, data.Rows[0][2]);
        Assert.Null(data.Rows[1][2]);
        Assert.Equal(4, data.Nodes.Count);
        definition.Nodes[2].Expression = "D + 1";
        Assert.NotNull(CustomPanelDefinition.Validate(definition));
    }

    [Fact]
    public async Task CustomPanel_AlignsExactKeysAndRejectsDuplicates()
    {
        var definition = new CustomPanelDefinition
        {
            Nodes =
            [
                new() { Ref = "A", Shape = "series", TimeField = "ts", Sql = "SELECT $start AS ts, 2 AS value UNION ALL SELECT $end AS ts, 3 AS value" },
                new() { Ref = "B", Shape = "series", TimeField = "ts", Sql = "SELECT $start AS ts, 4 AS value" },
                new() { Ref = "C", Kind = "expression", Expression = "A + B" },
            ],
            Outputs = ["C"],
        };
        var panel = new PanelConfig { Id = "custom", Query = new() { Kind = PanelQueryKinds.Custom, Custom = definition } };
        var data = await _executor.ExecuteAsync(panel, _now.AddHours(-1), _now);
        Assert.Null(data.Error);
        Assert.Equal(6d, data.Rows[0][2]);
        Assert.Null(data.Rows[1][2]);
        definition.Nodes[0].Sql = "SELECT $start AS ts, 2 AS value UNION ALL SELECT $start AS ts, 3 AS value";
        data = await _executor.ExecuteAsync(panel, _now.AddHours(-1), _now);
        Assert.Contains("重复", data.Error);
    }

    [Fact]
    public async Task CustomPanel_ReducesAndBroadcastsScalar()
    {
        var definition = new CustomPanelDefinition
        {
            Nodes =
            [
                new() { Ref = "A", Shape = "series", TimeField = "ts", Sql = "SELECT $start AS ts, 2 AS value UNION ALL SELECT $end AS ts, 4 AS value" },
                new() { Ref = "B", Kind = "expression", Expression = "A", Reduce = "avg" },
                new() { Ref = "C", Kind = "expression", Expression = "A / B" },
            ],
            Outputs = ["C"],
        };
        var panel = new PanelConfig { Id = "custom", Query = new() { Kind = PanelQueryKinds.Custom, Custom = definition } };
        var data = await _executor.ExecuteAsync(panel, _now.AddHours(-1), _now);
        Assert.Null(data.Error);
        Assert.Equal(2d / 3d, data.Rows[0][2]);
        Assert.Equal(4d / 3d, data.Rows[1][2]);
        Assert.NotNull(CustomPanelDefinition.Validate(definition, "stat"));
        Assert.Null(CustomPanelDefinition.Validate(definition, "line"));
    }

    /// <summary>
    /// metric 形态：两服务同指标不同值 → 按标签拆两条序列，avg 聚合正确.
    /// </summary>
    [Fact]
    public async Task MetricPanel_SplitsSeriesByLabels()
    {
        InsertMetric("http_duration", _now.AddMinutes(-1), 10, "{\"service\":\"api\"}");
        InsertMetric("http_duration", _now.AddMinutes(-1), 30, "{\"service\":\"db\"}");

        var data = await _executor.ExecuteAsync(new PanelConfig
        {
            Id = "p1",
            Query = new PanelQueryConfig
            {
                Kind = PanelQueryKinds.Metric,
                Name = "http_duration",
                Aggregation = "avg",
                StepSeconds = 60,
            },
        }, _now.AddHours(-1), _now.AddMinutes(1));

        Assert.Null(data.Error);
        Assert.Empty(data.Columns);
        Assert.Equal(2, data.Series.Count);
        var api = data.Series.Single(x => x.Labels.Contains("\"api\""));
        Assert.Equal(10, api.Points.Single().Value);
        var db = data.Series.Single(x => x.Labels.Contains("\"db\""));
        Assert.Equal(30, db.Points.Single().Value);
    }

    /// <summary>
    /// dsl 形态（metrics 域）：bucket 5m + avg(value) + groupBy labels.service → 每服务一条序列.
    /// </summary>
    [Fact]
    public async Task DslPanel_BucketSeries_GroupByLabel()
    {
        var t1 = _now.AddMinutes(-10);
        var t2 = _now.AddMinutes(-2);
        InsertMetric("cpu_usage", t1, 10, "{\"service\":\"api\"}");
        InsertMetric("cpu_usage", t2, 20, "{\"service\":\"api\"}");
        InsertMetric("cpu_usage", t1, 5, "{\"service\":\"db\"}");

        var data = await _executor.ExecuteAsync(new PanelConfig
        {
            Id = "p2",
            Query = new PanelQueryConfig
            {
                Kind = PanelQueryKinds.Dsl,
                Domain = "metrics",
                MaxSeries = 20,
                Dsl = new QueryDslModel
                {
                    Bucket = new QueryBucket { Step = "5m", Func = "avg", Field = "value" },
                    GroupBy = ["labels.service"],
                },
            },
        }, _now.AddHours(-1), _now.AddMinutes(1));

        Assert.Null(data.Error);
        Assert.Empty(data.Columns);
        Assert.Equal(2, data.Series.Count);
        var api = data.Series.Single(x => x.Labels.Contains("\"api\""));
        Assert.Equal(2, api.Points.Count);
        Assert.Contains(api.Points, x => x.Value == 10);
        Assert.Contains(api.Points, x => x.Value == 20);
        var db = data.Series.Single(x => x.Labels.Contains("\"db\""));
        Assert.Equal(5, db.Points.Single().Value);
    }

    /// <summary>
    /// dsl 形态（logs 域）：无 bucket 纯聚合分组 → 表格形态.
    /// </summary>
    [Fact]
    public async Task DslPanel_TableMode_WithoutBucket()
    {
        InsertLog(_now.AddMinutes(-1), "api", "error", "boom");
        InsertLog(_now.AddMinutes(-1), "api", "info", "ok");
        InsertLog(_now.AddMinutes(-1), "db", "error", "slow");

        var data = await _executor.ExecuteAsync(new PanelConfig
        {
            Id = "p3",
            Query = new PanelQueryConfig
            {
                Kind = PanelQueryKinds.Dsl,
                Domain = "logs",
                Dsl = new QueryDslModel
                {
                    Aggregations = [new QueryAggregation { Func = "count", Alias = "total" }],
                    GroupBy = ["service"],
                },
            },
        }, _now.AddHours(-1), _now.AddMinutes(1));

        Assert.Null(data.Error);
        Assert.Empty(data.Series);
        Assert.Contains("service", data.Columns);
        Assert.Contains("total", data.Columns);
        Assert.Equal(2, data.Rows.Count);
    }

    /// <summary>
    /// promql 形态：rate(counter[5m]) → 每秒增量序列.
    /// </summary>
    [Fact]
    public async Task PromqlPanel_Rate()
    {
        InsertMetric("http_requests_total", _now.AddSeconds(-300), 100, "{\"service\":\"api\"}");
        InsertMetric("http_requests_total", _now.AddSeconds(-150), 200, "{\"service\":\"api\"}");
        InsertMetric("http_requests_total", _now.AddSeconds(-1), 400, "{\"service\":\"api\"}");

        var data = await _executor.ExecuteAsync(new PanelConfig
        {
            Id = "p4",
            Query = new PanelQueryConfig
            {
                Kind = PanelQueryKinds.Promql,
                Expr = "rate(http_requests_total{service=\"api\"}[5m])",
                StepSeconds = 60,
            },
        }, _now.AddHours(-1), _now.AddMinutes(1));

        Assert.Null(data.Error);
        var series = Assert.Single(data.Series);
        Assert.Equal("http_requests_total", series.Name);
        // 修正后的 Prometheus 语义：每个评估点向前回看 5m（跨 step 桶）。
        // 样本 (100@-300s, 200@-150s, 400@-1s)、step=60s → 含数据桶 E∈{-240,-180,-120,-60,0,+60}，
        // rate 值依次 ≈ [0, 0, 0.333, 0.333, 1.0, 0.667]
        Assert.Equal(6, series.Points.Count);
        Assert.Equal(1.0, series.Points.Max(x => x.Value)!.Value, precision: 2);
        Assert.Contains(series.Points, x => Math.Abs(x.Value!.Value - 0.333) < 0.01);
        Assert.Contains(series.Points, x => Math.Abs(x.Value!.Value - 0.667) < 0.01);
        Assert.All(series.Points, x => Assert.True(x.Value!.Value >= 0));
    }

    /// <summary>
    /// 旧协议面板（无 query，根上 sql）→ 表格形态，向后兼容.
    /// </summary>
    [Fact]
    public async Task LegacySqlPanel_StillWorks()
    {
        InsertMetric("legacy_metric", _now, 1, "{}");
        var table = TimeTables.Name(TimeTables.MetricsPrefix, _now);

        var data = await _executor.ExecuteAsync(new PanelConfig
        {
            Id = "p5",
            Sql = $"SELECT count(*) AS c FROM {table}",
        }, _now.AddHours(-1), _now.AddMinutes(1));

        Assert.Null(data.Error);
        Assert.Empty(data.Series);
        Assert.Equal(["c"], data.Columns);
        Assert.Equal(1L, Convert.ToInt64(data.Rows[0][0]));
    }

    [Fact]
    public async Task QueryExecutionDoesNotDependOnEditorOrVisualization()
    {
        var panel = new PanelConfig
        {
            Id = "independent",
            Query = new PanelQueryConfig { Kind = PanelQueryKinds.Sql, Sql = "SELECT $value AS value", Parameters = new() { ["value"] = 42 } },
        };
        var original = await _executor.ExecuteAsync(panel, _now.AddHours(-1), _now);
        panel.Editor = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("""{"version":99,"sql":"SELECT 999"}""");
        panel.Visualization = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("""{"version":99}""");
        var updated = await _executor.ExecuteAsync(panel, _now.AddHours(-1), _now);
        Assert.Null(original.Error);
        Assert.Null(updated.Error);
        Assert.Equal(42, Convert.ToInt32(Assert.Single(updated.Rows)[0]));
        Assert.Equal(original.Columns, updated.Columns);
    }

    /// <summary>Explorer 保存的参数与自动粒度可执行.</summary>
    [Fact]
    public async Task ExplorerSql_BindsParametersAndAutoBucket()
    {
        InsertMetric("quoted'metric", _now, 7, "{}");
        var data = await _executor.ExecuteAsync(new PanelConfig
        {
            Id = "explorer",
            Query = new PanelQueryConfig
            {
                Kind = PanelQueryKinds.Sql,
                Sql = "SELECT time_bucket(CAST($chart_bucket AS INTERVAL), ts) AS ts_bucket, avg(value) AS value FROM {{metrics}} WHERE ts >= $start AND ts < $end AND name = $p0 GROUP BY ts_bucket",
                Parameters = new() { ["p0"] = "quoted'metric" },
                AutoGranularity = true,
            },
        }, _now.AddHours(-2), _now.AddMinutes(1));

        Assert.Null(data.Error);
        Assert.Single(data.Rows);
        Assert.Equal(7, Convert.ToDouble(data.Rows[0][1]));
        Assert.Equal(_now.AddHours(-2), data.From);
        Assert.Equal(_now.AddMinutes(1), data.To);
    }

    /// <summary>保存的 Bar 按面板窗口重新聚合，分类上限与筛选值均绑定.</summary>
    [Fact]
    public async Task BarSql_PreservesCategoriesAndBoundTopN()
    {
        InsertLog(_now.AddMinutes(-2), "api", "error'quoted", "one");
        InsertLog(_now.AddMinutes(-1), "api", "error'quoted", "two");
        InsertLog(_now.AddMinutes(-1), "db", "error'quoted", "three");
        InsertLog(_now.AddHours(-2), "outside", "error'quoted", "old");
        var panel = new PanelConfig
        {
            Id = "bar",
            Type = "bar",
            Query = new PanelQueryConfig
            {
                Kind = PanelQueryKinds.Sql,
                Sql = "SELECT service, count(*) AS value FROM {{logs}} WHERE ts >= $start AND ts < $end AND severity = $severity GROUP BY service ORDER BY value DESC, service ASC LIMIT $category_limit",
                Parameters = new() { ["severity"] = "error'quoted", ["category_limit"] = 1 },
                AutoGranularity = false,
            },
        };
        var data = await _executor.ExecuteAsync(panel, _now.AddHours(-1), _now);
        Assert.Null(data.Error);
        Assert.Empty(data.Series);
        var row = Assert.Single(data.Rows);
        Assert.Equal("api", row[data.Columns.IndexOf("service")]);
        Assert.Equal(2L, Convert.ToInt64(row[data.Columns.IndexOf("value")]));
        Assert.DoesNotContain("ts_bucket", data.Columns);
        var narrow = await _executor.ExecuteAsync(panel, _now.AddSeconds(-90), _now);
        Assert.Null(narrow.Error);
        Assert.Equal(1L, Convert.ToInt64(Assert.Single(narrow.Rows)[narrow.Columns.IndexOf("value")]));
    }

    /// <summary>Number 对完整窗口聚合，而非末桶值或桶均值.</summary>
    [Fact]
    public async Task NumberSql_AggregatesEntireWindowWithHalfOpenBounds()
    {
        var start = _now.AddMinutes(-10);
        InsertMetric("number", start.AddSeconds(1), 0, "{}");
        InsertMetric("number", start.AddSeconds(2), 0, "{}");
        InsertMetric("number", start.AddMinutes(8), 90, "{}");
        InsertMetric("number", _now, 999, "{}");
        var data = await _executor.ExecuteAsync(new PanelConfig
        {
            Id = "number",
            Query = new PanelQueryConfig
            {
                Kind = PanelQueryKinds.Sql,
                Sql = "SELECT count(*) AS total, avg(value) AS mean, quantile_cont(value, 0.95) AS p95 FROM {{metrics}} WHERE ts >= $start AND ts < $end AND name = $name",
                Parameters = new() { ["name"] = "number" },
            },
        }, start, _now);
        Assert.Null(data.Error);
        var row = Assert.Single(data.Rows);
        Assert.Equal(3, Convert.ToInt64(row[0]));
        Assert.Equal(30, Convert.ToDouble(row[1]));
        Assert.Equal(81, Convert.ToDouble(row[2]), 6);
    }

    /// <summary>公式按分组对齐，缺失输入和除零保持空值.</summary>
    [Fact]
    public async Task FormulaSql_AlignsGroupsAndGuardsDivision()
    {
        InsertMetric("A", _now, 20, "normal");
        InsertMetric("B", _now, 30, "normal");
        InsertMetric("A", _now, 20, "missing");
        InsertMetric("A", _now, 0, "zero");
        InsertMetric("B", _now, 0, "zero");
        const string sql = """
            WITH inputs AS (
              SELECT labels, 0 AS input_index, CAST(sum(value) AS DOUBLE) AS value FROM {{metrics}}
              WHERE ts >= $start AND ts < $end AND name = $a GROUP BY labels
              UNION ALL
              SELECT labels, 1 AS input_index, CAST(sum(value) AS DOUBLE) AS value FROM {{metrics}}
              WHERE ts >= $start AND ts < $end AND name = $b GROUP BY labels
            ), aligned AS (
              SELECT labels,
                max(CASE WHEN input_index = 0 AND isfinite(value) THEN value END) AS v0,
                max(CASE WHEN input_index = 1 AND isfinite(value) THEN value END) AS v1
              FROM inputs GROUP BY labels
            ), calculated AS (
              SELECT labels, $id AS __series_id, $alias AS series, true AS __formula,
                (v0 / NULLIF(v0 + v1, 0)) * $scale AS value FROM aligned
            )
            SELECT * REPLACE (CASE WHEN isfinite(value) THEN value END AS value)
            FROM calculated ORDER BY labels LIMIT 10000
            """;
        var data = await _executor.ExecuteAsync(new PanelConfig
        {
            Id = "formula",
            Query = new PanelQueryConfig
            {
                Kind = PanelQueryKinds.Sql,
                Sql = sql,
                Parameters = new() { ["a"] = "A", ["b"] = "B", ["id"] = "f1", ["alias"] = "Ratio'quoted", ["scale"] = 100 },
            },
        }, _now.AddHours(-1), _now.AddMinutes(1));
        Assert.Null(data.Error);
        Assert.Equal(3, data.Rows.Count);
        Assert.Null(data.Rows.Single(row => Equals(row[0], "missing"))[4]);
        Assert.Null(data.Rows.Single(row => Equals(row[0], "zero"))[4]);
        Assert.Equal(40, Convert.ToDouble(data.Rows.Single(row => Equals(row[0], "normal"))[4]));
    }

    /// <summary>
    /// 未知 kind → PanelData.Error，不抛异常中断.
    /// </summary>
    [Fact]
    public async Task UnknownKind_ReportsError()
    {
        var data = await _executor.ExecuteAsync(new PanelConfig
        {
            Id = "p6",
            Query = new PanelQueryConfig { Kind = "nope" },
        }, _now.AddHours(-1), _now.AddMinutes(1));

        Assert.NotNull(data.Error);
        Assert.Contains("nope", data.Error);
    }

    /// <summary>
    /// promql 语法错误 → PanelData.Error（含位置提示），不抛异常.
    /// </summary>
    [Fact]
    public async Task BadPromql_ReportsError()
    {
        var data = await _executor.ExecuteAsync(new PanelConfig
        {
            Id = "p7",
            Query = new PanelQueryConfig { Kind = PanelQueryKinds.Promql, Expr = "sum(" },
        }, _now.AddHours(-1), _now.AddMinutes(1));

        Assert.NotNull(data.Error);
        Assert.Contains("字符", data.Error);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // 临时目录清理失败可忽略
        }
    }

    private void InsertMetric(string name, DateTimeOffset ts, double value, string labels)
    {
        var sql = $"INSERT INTO {TimeTables.Name(TimeTables.MetricsPrefix, ts)} (name, ts, value, labels, kind) VALUES ($name, $ts, $value, $labels, 'gauge')";
        _connectionManager.ExecuteWriteAsync(sql,
        [
            new DuckDB.NET.Data.DuckDBParameter("name", name),
            new DuckDB.NET.Data.DuckDBParameter("ts", ts.ToUniversalTime().UtcDateTime),
            new DuckDB.NET.Data.DuckDBParameter("value", value),
            new DuckDB.NET.Data.DuckDBParameter("labels", labels),
        ]).GetAwaiter().GetResult();
    }

    private void InsertLog(DateTimeOffset ts, string service, string severity, string body)
    {
        var sql = $"INSERT INTO {TimeTables.Name(TimeTables.LogsPrefix, ts)} (id, ts, service, severity, body, attributes, resource) VALUES ($id, $ts, $service, $severity, $body, '{{}}', '{{}}')";
        _connectionManager.ExecuteWriteAsync(sql,
        [
            new DuckDB.NET.Data.DuckDBParameter("id", Random.Shared.NextInt64(1_000_000)),
            new DuckDB.NET.Data.DuckDBParameter("ts", ts.ToUniversalTime().UtcDateTime),
            new DuckDB.NET.Data.DuckDBParameter("service", service),
            new DuckDB.NET.Data.DuckDBParameter("severity", severity),
            new DuckDB.NET.Data.DuckDBParameter("body", body),
        ]).GetAwaiter().GetResult();
    }

    private sealed class LocalOnlyResolver(DuckDbConnectionManager manager) : ITableResolver
    {
        public Task<ResolvedTables> ResolveAsync(DataDomain domain, DateTimeOffset from, DateTimeOffset to, string? searchKeyword = null, CancellationToken cancellationToken = default)
        {
            var prefix = domain switch
            {
                DataDomain.Logs => TimeTables.LogsPrefix,
                DataDomain.Traces => TimeTables.TracesPrefix,
                DataDomain.Iot => TimeTables.IotPrefix,
                _ => TimeTables.MetricsPrefix,
            };
            var tables = manager.GetDayTables(prefix, from, to);
            return Task.FromResult(new ResolvedTables(tables, Array.Empty<string>()));
        }
    }
}
