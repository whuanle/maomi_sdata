using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Infra.Exceptions;
using SData.Settings;
using SData.Settings.Services;
using SData.Store;
using SData.Store.Core;
using SData.Store.Models;
using SData.Store.QueryDsl;

namespace SData.Store.Tests;

/// <summary>
/// 统一搜索 SQL 直查测试：表占位符展开（SearchSqlExpander）、$start/$end 参数绑定、只读约束与真实 DuckDB 执行链路.
/// </summary>
[Collection("content-root-serial")]
public partial class AnalyticsSearchSqlTests(global::Xunit.Abstractions.ITestOutputHelper output)
{
    // ---------- SearchSqlExpander 纯逻辑 ----------

    /// <summary>表占位符展开为按天表 UNION ALL BY NAME 子查询（按列名合并，兼容列序不同的天表）.</summary>
    [Fact]
    public void Expand_ReplacesTokenWithDayTableUnion()
    {
        var result = SearchSqlExpander.Expand(
            "SELECT ts FROM {{logs}} WHERE severity = 'ERROR'",
            _ => new ResolvedTables(["logs_20260101", "logs_20260102"], Array.Empty<string>()));

        Assert.NotNull(result);
        Assert.Contains("SELECT * FROM logs_20260101 UNION ALL BY NAME SELECT * FROM logs_20260102", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("{{logs}}", result.Sql, StringComparison.Ordinal);
        Assert.False(result.UsesStart);
        Assert.False(result.UsesEnd);
    }

    /// <summary>字符串字面量内的占位符与 $start 不展开、不检测.</summary>
    [Fact]
    public void Expand_SkipsStringLiterals()
    {
        var result = SearchSqlExpander.Expand(
            "SELECT '{{logs}} $start' AS note FROM {{traces}} WHERE ts >= $start AND ts < $end",
            _ => new ResolvedTables(["traces_20260101"], Array.Empty<string>()));

        Assert.NotNull(result);
        Assert.Contains("'{{logs}} $start'", result.Sql, StringComparison.Ordinal);
        Assert.Contains("SELECT * FROM traces_20260101", result.Sql, StringComparison.Ordinal);
        Assert.True(result.UsesStart);
        Assert.True(result.UsesEnd);
    }

    /// <summary>未知占位符拒绝（400）.</summary>
    [Fact]
    public void Expand_UnknownToken_Throws400()
    {
        var exception = Assert.Throws<BusinessException>(
            () => SearchSqlExpander.Expand("SELECT * FROM {{rum}}", _ => new ResolvedTables(["logs_20260101"], Array.Empty<string>())));
        Assert.Equal(400, exception.StatusCode);
    }

    /// <summary>未闭合占位符拒绝（400）.</summary>
    [Fact]
    public void Expand_MissingClose_Throws400()
    {
        var exception = Assert.Throws<BusinessException>(
            () => SearchSqlExpander.Expand("SELECT * FROM {{logs", _ => new ResolvedTables(["logs_20260101"], Array.Empty<string>())));
        Assert.Equal(400, exception.StatusCode);
    }

    /// <summary>占位符允许两侧空白（{{ logs }} 同样展开）.</summary>
    [Fact]
    public void Expand_AllowsWhitespaceInsideToken()
    {
        var result = SearchSqlExpander.Expand("SELECT count(*) FROM {{ logs }}", _ => new ResolvedTables(["logs_20260101"], Array.Empty<string>()));
        Assert.NotNull(result);
        Assert.Contains("FROM (SELECT * FROM logs_20260101)", result.Sql, StringComparison.Ordinal);
    }

    /// <summary>区间内无现存天表：返回 null（调用方短路空结果）.</summary>
    [Fact]
    public void Expand_NoTables_ReturnsNull()
    {
        var result = SearchSqlExpander.Expand("SELECT * FROM {{logs}}", _ => new ResolvedTables(Array.Empty<string>(), Array.Empty<string>()));
        Assert.Null(result);
    }

    // ---------- AnalyticsExecutor.ExecuteSearchAsync 端到端 ----------

    /// <summary>端到端：落库 → {{logs}} 展开 + $start/$end 绑定 → 返回行.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_TokenAndTimeParams_ReturnsRows()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "web", Severity = "ERROR", Body = "boom" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "db", Severity = "INFO", Body = "ok" }),
        }, harness.Token);

        var sql = "SELECT service, severity, body FROM {{logs}} WHERE ts >= $start AND ts < $end AND severity = 'ERROR' ORDER BY ts";
        var result = await harness.Executor.ExecuteSearchAsync(sql, now.AddMinutes(-5), now.AddMinutes(5), 100, cancellationToken: harness.Token);

        var row = Assert.Single(result.Rows);
        Assert.Equal("web", row[result.Columns.IndexOf("service")]);
        Assert.Equal("ERROR", row[result.Columns.IndexOf("severity")]);
        Assert.False(result.Truncated);
    }

    /// <summary>冷层端到端（UI 统一搜索主通道）：台账分片经 {{logs}} 展开的 read_parquet 分支可查.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_ColdFiles_Returned()
    {
        using var harness = new SearchHarness();
        var coldPath = Path.Combine(harness.TempDirectory, "data", "cold", "logs", "20200101.parquet").Replace('\\', '/');
        Directory.CreateDirectory(Path.GetDirectoryName(coldPath)!);
        harness.Manager.ExecuteWriteAsync(
            $"""COPY (SELECT 7 AS id, TIMESTAMP '2020-01-01 08:00:00' AS ts, 'cold-svc' AS service, 'ERROR' AS severity, '历史归档日志' AS body, NULL AS trace_id, NULL AS span_id, NULL AS attributes, NULL AS resource, NULL AS source) TO '{coldPath}' (FORMAT PARQUET)""").GetAwaiter().GetResult();
        harness.Ledger.ColdArchives.Add(new SData.Database.Entities.ColdArchiveEntity { Domain = "logs", Day = "20200101", ObjectKey = "data/cold/logs/20200101.parquet", RowCount = 1, ArchivedTime = DateTimeOffset.UtcNow });
        await harness.Ledger.SaveChangesAsync(harness.Token);

        var sql = "SELECT service, body FROM {{logs}} WHERE ts >= $start AND ts < $end AND body LIKE $kw ORDER BY ts";

        // analytics/search 是原生 SQL 通道：LIKE 通配由调用方负责（% 归 %），区别于 QueryDsl match 的自动子串包装
        var result = await harness.Executor.ExecuteSearchAsync(
            sql,
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero),
            100,
            new Dictionary<string, object?> { ["kw"] = "%归档%" },
            harness.Token);

        Assert.True(result.Rows.Count == 1, $"rows={result.Rows.Count}, cols=[{string.Join(",", result.Columns)}], truncated={result.Truncated}");
        Assert.Equal("cold-svc", result.Rows[0][result.Columns.IndexOf("service")]);
    }

    /// <summary>区间内占位符无现存天表：短路返回空结果.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_EmptyRangeTables_ReturnsEmpty()
    {
        using var harness = new SearchHarness();
        var result = await harness.Executor.ExecuteSearchAsync(
            "SELECT * FROM {{logs}} WHERE ts >= $start",
            new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(1980, 1, 2, 0, 0, 0, TimeSpan.Zero),
            100,
            cancellationToken: harness.Token);

        Assert.Empty(result.Columns);
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.ElapsedMs);
    }

    /// <summary>
    /// 回归：列序不同的天表（旧版迁移表 id/source 追加在末尾，新表 id 在首列）跨天 UNION 按列名合并；
    /// 位置合并会让 id(BIGINT) 对上 ts(TIMESTAMP) 抛「Unimplemented type for cast」导致搜索 500.
    /// </summary>
    [Fact]
    public async Task ExecuteSearchAsync_MixedColumnOrderDayTables_UnionByName()
    {
        using var harness = new SearchHarness();
        var day1 = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var table1 = TimeTables.Name(TimeTables.LogsPrefix, day1);
        var table2 = TimeTables.Name(TimeTables.LogsPrefix, day2);

        // 旧版天表（经 T121 迁移：source/id 追加在末尾），列序与新版 DDL（id 首列）不同
        harness.Manager.ExecuteWriteAsync(
            $"CREATE TABLE {table1} (ts TIMESTAMP NOT NULL, service VARCHAR NOT NULL, severity VARCHAR NOT NULL, body VARCHAR NOT NULL, trace_id VARCHAR, span_id VARCHAR, attributes VARCHAR, resource VARCHAR, id BIGINT, source VARCHAR)").GetAwaiter().GetResult();
        harness.Manager.ExecuteWriteAsync(
            $"INSERT INTO {table1} VALUES (TIMESTAMP '2020-01-01 08:00:00', 'web', 'ERROR', 'legacy-row', NULL, NULL, NULL, NULL, 1001, NULL)").GetAwaiter().GetResult();
        // 注册进托管表清单（CREATE TABLE IF NOT EXISTS 对已建表为 no-op，保留旧列序不重建）
        harness.Manager.EnsureDayTablesAsync(TimeTables.LogsPrefix, new[] { day1, day2 }, harness.Token).GetAwaiter().GetResult();
        harness.Manager.ExecuteWriteAsync(
            $"INSERT INTO {table2} VALUES (1002, TIMESTAMP '2020-01-02 08:00:00', 'web', 'INFO', 'new-row', NULL, NULL, NULL, NULL, NULL)").GetAwaiter().GetResult();

        var result = await harness.Executor.ExecuteSearchAsync(
            "SELECT id, ts, service, body FROM {{logs}} WHERE ts >= $start AND ts < $end ORDER BY ts",
            day1,
            new DateTimeOffset(2020, 1, 3, 0, 0, 0, TimeSpan.Zero),
            100,
            cancellationToken: harness.Token);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(1001L, Convert.ToInt64(result.Rows[0][result.Columns.IndexOf("id")]));
        Assert.Equal("legacy-row", result.Rows[0][result.Columns.IndexOf("body")]);
        Assert.Equal(1002L, Convert.ToInt64(result.Rows[1][result.Columns.IndexOf("id")]));
        Assert.Equal("new-row", result.Rows[1][result.Columns.IndexOf("body")]);
    }


    /// <summary>
    /// 回归（UI 过滤器树 facet 聚合）：行抽样 USING SAMPLE 固定行数 + json_keys 枚举在 {{logs}} 展开子查询上可执行，
    /// 计数为样本估计（ClickStack 式）；WHERE → USING SAMPLE 语法顺序经真实 DuckDB 验证（本仓库 DuckDB 不接受 ROWS 后跟种子）.
    /// </summary>
    [Fact]
    public async Task ExecuteSearchAsync_FacetKeysSql_SampledAggregation_ReturnsCounts()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "web", Severity = "ERROR", Body = "a", AttributesJson = "{\"pay.channel\":\"alipay\"}" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "db", Severity = "INFO", Body = "b", AttributesJson = "{\"pay.channel\":\"wechat\",\"sku\":\"x1\"}" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "web", Severity = "INFO", Body = "c", AttributesJson = "not-json" }),
        }, harness.Token);

        // 与前端 facets.ts buildGroupKeysSql 逐字同构（时间参数服务端绑定，行抽样 1000 条）
        var sql = """
            SELECT v, count(*) AS c
            FROM (SELECT unnest(json_keys(CASE WHEN json_valid(attributes) THEN attributes ELSE '{}' END)) AS v FROM {{logs}} WHERE ts >= $start AND ts < $end USING SAMPLE 1000 ROWS)
            WHERE v IS NOT NULL
            GROUP BY v
            ORDER BY c DESC, v ASC
            LIMIT 10
            """;
        var result = await harness.Executor.ExecuteSearchAsync(sql, now.AddMinutes(-5), now.AddMinutes(5), 10_000, cancellationToken: harness.Token);

        Assert.True(result.Rows.Count >= 2, $"rows={result.Rows.Count}, cols=[{string.Join(",", result.Columns)}]");
        Assert.Contains("pay.channel", result.Rows.Select(row => row[result.Columns.IndexOf("v")]));
        Assert.Contains("sku", result.Rows.Select(row => row[result.Columns.IndexOf("v")]));
    }

    /// <summary>
    /// 回归（UI 过滤器树属性/值搜索）：搜索态不加行抽样（ILIKE 扫描整个搜索时间窗，样本外稀有值可搜）；
    /// ILIKE $key + ESCAPE 字面转义在参数绑定下可执行且过滤生效（下划线按字面匹配）.
    /// </summary>
    [Fact]
    public async Task ExecuteSearchAsync_FacetValuesSql_IlikeEscapeParam_Filters()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "api-gateway", Severity = "ERROR", Body = "a" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "order_service", Severity = "INFO", Body = "b" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "payment", Severity = "INFO", Body = "c" }),
        }, harness.Token);

        // 与前端 facets.ts buildFieldValuesSql 同构（搜索态）：值分布 + 搜索词 'order_service'（%_% 转义后按字面匹配，不再通配任意字符）
        var sql = """
            SELECT v, count(*) AS c
            FROM (SELECT service AS v FROM {{logs}} WHERE ts >= $start AND ts < $end)
            WHERE v IS NOT NULL AND v ILIKE $key ESCAPE '\'
            GROUP BY v
            ORDER BY c DESC, v ASC
            LIMIT 10
            """;
        var result = await harness.Executor.ExecuteSearchAsync(
            sql, now.AddMinutes(-5), now.AddMinutes(5), 10_000,
            new Dictionary<string, object?> { ["key"] = "%order\\_service%" },
            harness.Token);

        var row = Assert.Single(result.Rows);
        Assert.Equal("order_service", row[result.Columns.IndexOf("v")]);
    }

    /// <summary>
    /// 回归（UI JSON 属性发现/过滤）：点分字面 key 与括号嵌套路径同列共存互不串查；
    /// 提取路径走服务端命名参数绑定（$jpN），json_valid 防护使普通文本 body 不抛错；数组按显式索引提取.
    /// </summary>
    [Fact]
    public async Task ExecuteSearchAsync_JsonNestedPathParam_Extraction_DistinguishesLiteralDottedKeys()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            // 字面点分 key：attributes 整列是 {"http.status_code":"500"}（单层 key 里带点）
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "web", Severity = "ERROR", Body = "dotted", AttributesJson = "{\"http.status_code\":\"500\"}" }),
            // 真嵌套：attributes 整列是 {"http":{"status_code":500}}（嵌套对象）
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "api", Severity = "ERROR", Body = "nested", AttributesJson = "{\"http\":{\"status_code\":500}}" }),
            // body 为 JSON 数组文档（ES Bulk 式整文档入 body）+ 文本 body 混存
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "bulk", Severity = "INFO", Body = "{\"items\":[{\"name\":\"ok\"}]}" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "text", Severity = "INFO", Body = "plain text body" }),
        }, harness.Token);

        // 与前端 lucene.ts jsonFieldExpression 同构：json_valid 防护 + 绑定路径（$jpN）
        const string guarded = "json_extract_string(CASE WHEN json_valid({0}) THEN {0} ELSE NULL END, $jp0)";
        var literal = await harness.Executor.ExecuteSearchAsync(
            $"SELECT {string.Format(guarded, "attributes")} AS v FROM {{{{logs}}}} WHERE ts >= $start AND ts < $end",
            now.AddMinutes(-5), now.AddMinutes(5), 10_000,
            new Dictionary<string, object?> { ["jp0"] = "$.\"http.status_code\"" }, harness.Token);
        Assert.Equal("500", Assert.Single(literal.Rows.Where(row => row[0] is not null))[0]);

        var nested = await harness.Executor.ExecuteSearchAsync(
            $"SELECT {string.Format(guarded, "attributes")} AS v FROM {{{{logs}}}} WHERE ts >= $start AND ts < $end",
            now.AddMinutes(-5), now.AddMinutes(5), 10_000,
            new Dictionary<string, object?> { ["jp0"] = "$.\"http\".\"status_code\"" }, harness.Token);
        Assert.Equal("500", Assert.Single(nested.Rows.Where(row => row[0] is not null))[0]);

        // 嵌套路径不命中字面 key 行、字面路径不命中嵌套行（互不串查）：两查询均只回 1 个非空值
        var indexed = await harness.Executor.ExecuteSearchAsync(
            $"SELECT {string.Format(guarded, "body")} AS v FROM {{{{logs}}}} WHERE ts >= $start AND ts < $end",
            now.AddMinutes(-5), now.AddMinutes(5), 10_000,
            new Dictionary<string, object?> { ["jp0"] = "$.\"items\"[0].\"name\"" }, harness.Token);
        Assert.Equal("ok", Assert.Single(indexed.Rows.Where(row => row[0] is not null))[0]);
    }

    /// <summary>
    /// 回归（UI 发现树子属性枚举）：json_extract 按绑定路径取节点 + json_keys 枚举（buildChildKeysSql 同构），
    /// 根列/节点双重 json_valid 防护；对象节点返回 key 列表.
    /// </summary>
    [Fact]
    public async Task ExecuteSearchAsync_ChildKeysSql_ExtractedNode_ReturnsKeys()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "api", Severity = "ERROR", Body = "a", AttributesJson = "{\"http\":{\"status_code\":500,\"method\":\"POST\"}}" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "web", Severity = "INFO", Body = "b", AttributesJson = "not-json" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "bulk", Severity = "INFO", Body = "{\"items\":[{\"name\":\"ok\"},{\"name\":\"bad\"}]}" }),
        }, harness.Token);

        // 与前端 facets.ts buildChildKeysSql 同构（抽样口径同组 key 枚举）
        var sql = """
            SELECT v, count(*) AS c
            FROM (
                SELECT unnest(json_keys(CASE WHEN json_valid(CASE WHEN json_valid(attributes) THEN json_extract(attributes, $jp0) ELSE NULL END) THEN CASE WHEN json_valid(attributes) THEN json_extract(attributes, $jp0) ELSE NULL END ELSE '{}' END)) AS v
                FROM {{logs}} WHERE ts >= $start AND ts < $end USING SAMPLE 1000 ROWS
            )
            WHERE v IS NOT NULL
            GROUP BY v
            ORDER BY c DESC, v ASC
            LIMIT 10
            """;
        var nested = await harness.Executor.ExecuteSearchAsync(
            sql, now.AddMinutes(-5), now.AddMinutes(5), 10_000,
            new Dictionary<string, object?> { ["jp0"] = "$.\"http\"" }, harness.Token);

        Assert.Equal(2, nested.Rows.Count);
        Assert.Contains("status_code", nested.Rows.Select(row => row[nested.Columns.IndexOf("v")]));
        Assert.Contains("method", nested.Rows.Select(row => row[nested.Columns.IndexOf("v")]));
    }

    /// <summary>回归（UI JSON 结果列）：投影表达式以显示名作带引号列别名（括号/引号形态）在 DuckDB 可执行，列名原样返回.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_JsonProjectionQuotedAlias_ReturnsColumnName()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "api", Severity = "ERROR", Body = "a", AttributesJson = "{\"http\":{\"status_code\":500}}" }),
        }, harness.Token);

        var sql = """
            SELECT json_extract_string(CASE WHEN json_valid(attributes) THEN attributes ELSE NULL END, $jp0) AS "attributes[""http""][""status_code""]"
            FROM {{logs}} WHERE ts >= $start AND ts < $end
            """;
        var result = await harness.Executor.ExecuteSearchAsync(
            sql, now.AddMinutes(-5), now.AddMinutes(5), 100,
            new Dictionary<string, object?> { ["jp0"] = "$.\"http\".\"status_code\"" }, harness.Token);

        Assert.Contains("attributes[\"http\"][\"status_code\"]", result.Columns);
        Assert.Equal("500", Assert.Single(result.Rows)[0]);
    }

    /// <summary>写语句拒绝（执行器层 ArgumentException，Handler 层转 400）.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_RejectsWrite()
    {
        using var harness = new SearchHarness();
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Executor.ExecuteSearchAsync("DELETE FROM logs_x", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, 100, cancellationToken: harness.Token));
    }

    /// <summary>多语句拒绝（执行器层 ArgumentException，Handler 层转 400）.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_RejectsMultiStatement()
    {
        using var harness = new SearchHarness();
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Executor.ExecuteSearchAsync("SELECT 1; SELECT 2", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, 100, cancellationToken: harness.Token));
    }

    /// <summary>上下文分页：绑定分页参数、多查一行探测与大整数 ID 字符串保持.</summary>
    [Theory]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(200)]
    [InlineData(201)]
    public async Task ExecuteSearchAsync_ContextPages_ProbeAndAppend(int count)
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        var items = Enumerable.Range(0, count)
            .Select(index => new IngestItem(IngestKind.Log, new LogRecord
            {
                Id = 9007199254740993L + index,
                Timestamp = now,
                Service = "web",
                Severity = "INFO",
                Body = index.ToString(),
            })).ToList();
        await harness.Writer.FlushAsync(items, harness.Token);

        var sql = "SELECT CAST(id AS VARCHAR) AS id, ts, service, severity, body, trace_id, span_id FROM {{logs}} "
            + "WHERE ts >= $start AND ts < $end "
            + "ORDER BY ts ASC, id ASC NULLS LAST, service ASC, severity ASC, body ASC, trace_id ASC, span_id ASC "
            + "LIMIT CAST($page_limit AS BIGINT) OFFSET CAST($page_offset AS BIGINT)";
        var ids = new List<string>();
        var hasMore = true;
        while (hasMore)
        {
            var result = await harness.Executor.ExecuteSearchAsync(
                sql, now.AddMinutes(-5), now.AddMinutes(5), 100,
                new Dictionary<string, object?> { ["page_limit"] = 101d, ["page_offset"] = (double)ids.Count },
                harness.Token);
            Assert.InRange(result.Rows.Count, 1, 100);
            ids.AddRange(result.Rows.Select(row => Assert.IsType<string>(row[0])));
            Assert.Equal(ids.Count < count, result.Truncated);
            hasMore = result.Truncated;
        }

        Assert.Equal(count, ids.Count);
        Assert.Equal(count, ids.Distinct().Count());
        Assert.Contains("9007199254740993", ids);
    }

    /// <summary>行数上限截断.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_RowLimit_Truncates()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddSeconds(-3), Service = "web", Severity = "INFO", Body = "a" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddSeconds(-2), Service = "web", Severity = "INFO", Body = "b" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddSeconds(-1), Service = "web", Severity = "INFO", Body = "c" }),
        }, harness.Token);

        var result = await harness.Executor.ExecuteSearchAsync(
            "SELECT body FROM {{logs}} WHERE ts >= $start AND ts < $end ORDER BY body",
            now.AddMinutes(-5), now.AddMinutes(5), 2, cancellationToken: harness.Token);

        Assert.Equal(2, result.Rows.Count);
        Assert.True(result.Truncated);
    }

    /// <summary>查询跨度超过 MaxQueryDays 拒绝（400）.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_RejectsSpanBeyondMaxQueryDays()
    {
        using var harness = new SearchHarness(options => options.DuckDb.MaxQueryDays = 7);
        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => harness.Executor.ExecuteSearchAsync("SELECT * FROM {{logs}}", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, 100, cancellationToken: harness.Token));
        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("MaxQueryDays", exception.Message, StringComparison.Ordinal);
    }

    // ---------- 命名参数绑定（$p0..$pN，UI 属性搜索编译产物） ----------

    /// <summary>Expand 收集字符串字面量之外的 $name 引用（去重、不含 $ 前缀），字面量内不收集.</summary>
    [Fact]
    public void Expand_CollectsReferencedParams()
    {
        var result = SearchSqlExpander.Expand(
            "SELECT * FROM {{logs}} WHERE severity = $p0 AND body LIKE $p1 OR severity = $p0 AND note = 'literal $p2'",
            _ => new ResolvedTables(["logs_20260101"], Array.Empty<string>()));

        Assert.NotNull(result);
        Assert.Equal(new[] { "p0", "p1" }, result.ReferencedParams);
        Assert.False(result.UsesStart);
    }

    /// <summary>端到端：命名参数绑定过滤生效（数值与字符串各一）.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_NamedParams_AreBound()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "web", Severity = "ERROR", Body = "boom" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "db", Severity = "INFO", Body = "ok" }),
        }, harness.Token);

        var sql = "SELECT service FROM {{logs}} WHERE ts >= $start AND ts < $end AND severity = $p0 AND body LIKE $p1 ORDER BY service";
        var result = await harness.Executor.ExecuteSearchAsync(
            sql, now.AddMinutes(-5), now.AddMinutes(5), 100,
            new Dictionary<string, object?> { ["p0"] = "ERROR", ["p1"] = "%bo%m%" },
            harness.Token);

        var row = Assert.Single(result.Rows);
        Assert.Equal("web", row[result.Columns.IndexOf("service")]);
    }

    /// <summary>参数值含写关键字不再被黑名单误杀（值走参数绑定，不进入 SQL 文本）.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_ParamValueWithKeyword_NotRejected()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "web", Severity = "INFO", Body = "please delete this record" }),
        }, harness.Token);

        var sql = "SELECT body FROM {{logs}} WHERE ts >= $start AND ts < $end AND body LIKE $p0";
        var result = await harness.Executor.ExecuteSearchAsync(
            sql, now.AddMinutes(-5), now.AddMinutes(5), 100,
            new Dictionary<string, object?> { ["p0"] = "%delete%" },
            harness.Token);

        Assert.Single(result.Rows);
    }

    /// <summary>SQL 引用了未提供的命名参数：400（需区间内存在天表以通过短路检查）.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_MissingParam_Returns400()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "web", Severity = "INFO", Body = "ok" }),
        }, harness.Token);

        var exception = await Assert.ThrowsAsync<BusinessException>(
            () => harness.Executor.ExecuteSearchAsync(
                "SELECT * FROM {{logs}} WHERE severity = $p0",
                now.AddMinutes(-5), now.AddMinutes(5), 100,
                new Dictionary<string, object?>(), harness.Token));
        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("$p0", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>客户端会话占位符只解析内部天表，参数仍需绑定.</summary>
    [Fact]
    public async Task SessionsToken_ExecutesReadOnlyBoundQuery()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Manager.EnsureDayTablesAsync(TimeTables.RumSessionPrefix, new[] { now }, harness.Token);
        var result = await harness.Executor.ExecuteSearchAsync(
            "SELECT count(*) AS value FROM {{sessions}} WHERE ts >= $start AND ts < $end AND project_key = $project",
            now.AddMinutes(-5), now.AddMinutes(5), 100,
            new Dictionary<string, object?> { ["project"] = "quoted'project" }, harness.Token);
        Assert.Single(result.Rows);
        Assert.Equal(0L, Convert.ToInt64(result.Rows[0][0]));
    }

    /// <summary>模式查询使用前端生成的同一 SQL，完整窗口计数与趋势一致.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_Patterns_CountsAndTrendsAgree()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-2), Service = "web", Severity = "INFO", Body = "订单 123 已完成" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "web", Severity = "INFO", Body = "订单 456 已完成" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "web", Severity = "ERROR", Body = "订单 789 已完成" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddHours(-2), Service = "web", Severity = "INFO", Body = "订单 999 已完成" }),
        }, harness.Token);

        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "patterns.json");
        using var fixture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath, harness.Token));
        var parameters = fixture.RootElement.GetProperty("parameters").EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.GetString());
        var result = await harness.Executor.ExecuteSearchAsync(
            fixture.RootElement.GetProperty("sql").GetString()!, now.AddMinutes(-5), now, 200, parameters, harness.Token);

        Assert.Equal(2, result.Rows.Count);
        var first = result.Rows[0];
        Assert.Equal("订单 <*> 已完成", first[result.Columns.IndexOf("pattern")]);
        Assert.Equal(2L, Convert.ToInt64(first[result.Columns.IndexOf("count")]));
        Assert.Equal("info", first[result.Columns.IndexOf("level")]);
        using var trend = System.Text.Json.JsonDocument.Parse(Convert.ToString(first[result.Columns.IndexOf("trend")])!);
        Assert.Equal(2L, trend.RootElement.EnumerateArray().Sum(bin => bin.GetProperty("count").GetInt64()));
        using var sample = System.Text.Json.JsonDocument.Parse(Convert.ToString(first[result.Columns.IndexOf("sample")])!);
        Assert.Equal("订单 456 已完成", sample.RootElement.GetProperty("body").GetString());
    }

    /// <summary>热力图使用前端生成的 SQL，全量分桶保留边界并排除窗口外数据.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_Heatmap_CountsFullDistribution()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Trace, new SpanRecord { Timestamp = now.AddMinutes(-2), Service = "api", TraceId = "h1", SpanId = "s1", Name = "request", DurationUs = 1000, Status = "ok" }),
            new(IngestKind.Trace, new SpanRecord { Timestamp = now.AddMinutes(-1), Service = "api", TraceId = "h2", SpanId = "s2", Name = "request", DurationUs = 1000, Status = "ok" }),
            new(IngestKind.Trace, new SpanRecord { Timestamp = now.AddMinutes(-1), Service = "api", TraceId = "h3", SpanId = "s3", Name = "request", DurationUs = 9000, Status = "ok" }),
            new(IngestKind.Trace, new SpanRecord { Timestamp = now.AddHours(-2), Service = "api", TraceId = "h4", SpanId = "s4", Name = "request", DurationUs = 100000, Status = "ok" }),
        }, harness.Token);
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "heatmap.json");
        using var fixture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath, harness.Token));
        var parameters = fixture.RootElement.GetProperty("parameters").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                ? (object?)property.Value.GetInt32() : property.Value.GetString());
        var result = await harness.Executor.ExecuteSearchAsync(
            fixture.RootElement.GetProperty("sql").GetString()!, now.AddMinutes(-5), now, 10000, parameters, harness.Token);
        Assert.Equal(3L, result.Rows.Sum(row => Convert.ToInt64(row[result.Columns.IndexOf("value")])));
        Assert.Contains(result.Rows, row => Convert.ToInt32(row[result.Columns.IndexOf("y_bucket")]) == 39);
        Assert.All(result.Rows, row => Assert.Equal(60000d, Convert.ToDouble(row[result.Columns.IndexOf("__bucket_ms")])));
    }

    /// <summary>对数与线性分桶保留零值、负指标与长尾，格子计数守恒.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_Heatmap_ScalesPreserveAllSamples()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        var durations = new long[] { 0, 1, 999, 1000, 9999, 100000, 10000000 };
        var items = durations.Select((duration, index) => new IngestItem(IngestKind.Trace, new SpanRecord
        {
            Timestamp = now.AddMinutes(-2), Service = "api", TraceId = $"scale{index}", SpanId = $"s{index}",
            Name = "request", DurationUs = duration, Status = "ok",
        })).ToList();
        items.Add(new IngestItem(IngestKind.Trace, new SpanRecord
        {
            Timestamp = now.AddHours(-2), Service = "api", TraceId = "outside", SpanId = "old",
            Name = "request", DurationUs = 999999999, Status = "ok",
        }));
        var metricValues = new double[] { -10000, -1, 0, 1, 10000 };
        items.AddRange(metricValues.Select(value => new IngestItem(IngestKind.Metric, new MetricPointRecord
        {
            Name = "api", Timestamp = now.AddMinutes(-2), Value = value,
        })));
        await harness.Writer.FlushAsync(items, harness.Token);
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "heatmap-scales.json");
        using var fixture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path, harness.Token));
        foreach (var item in fixture.RootElement.EnumerateArray())
        {
            var parameters = item.GetProperty("parameters").EnumerateObject().ToDictionary(property => property.Name,
                property => property.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? (object?)property.Value.GetDouble() : property.Value.GetString());
            var sql = item.GetProperty("sql").GetString()!;
            var result = await harness.Executor.ExecuteSearchAsync(sql, now.AddMinutes(-5), now, 10000, parameters, harness.Token);
            if (item.GetProperty("domain").GetString() == "metrics")
            {
                Assert.Equal(metricValues.Length, result.Rows.Sum(row => Convert.ToInt32(row[result.Columns.IndexOf("value")])));
                Assert.Contains(result.Rows, row => Convert.ToDouble(row[result.Columns.IndexOf("bucket_min")]) < 0);
                continue;
            }

            Assert.False(result.Truncated);
            Assert.Equal(durations.Length, result.Rows.Sum(row => Convert.ToInt32(row[result.Columns.IndexOf("value")])));
            Assert.Contains(result.Rows, row => Convert.ToDouble(row[result.Columns.IndexOf("bucket_min")]) == 0);
            Assert.True(result.Rows.Count <= 9600);
            foreach (var row in result.Rows)
            {
                var lower = Convert.ToDouble(row[result.Columns.IndexOf("bucket_min")]);
                var upper = Convert.ToDouble(row[result.Columns.IndexOf("bucket_max")]);
                Assert.True(upper > lower);
            }

            var constantSql = sql.Replace("service = $service", "service = $service AND duration_us = 1000", StringComparison.Ordinal);
            var constant = await harness.Executor.ExecuteSearchAsync(constantSql, now.AddMinutes(-5), now, 10000, parameters, harness.Token);
            Assert.Single(constant.Rows);
            Assert.Equal(1L, Convert.ToInt64(constant.Rows[0][constant.Columns.IndexOf("value")]));
        }
    }

    /// <summary>表格使用前端 SQL 夹具验证全窗口分位数、宽列、公式与 Top N.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_Table_AggregatesBeforeTopN()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Trace, new SpanRecord { Timestamp = now.AddMinutes(-2), Service = "api", TraceId = "t1", SpanId = "s1", Name = "slow", DurationUs = 1000, Status = "ok" }),
            new(IngestKind.Trace, new SpanRecord { Timestamp = now.AddMinutes(-1), Service = "api", TraceId = "t2", SpanId = "s2", Name = "slow", DurationUs = 9000, Status = "error" }),
            new(IngestKind.Trace, new SpanRecord { Timestamp = now.AddMinutes(-1), Service = "db", TraceId = "t3", SpanId = "s3", Name = "fast", DurationUs = 100, Status = "ok" }),
            new(IngestKind.Trace, new SpanRecord { Timestamp = now.AddHours(-2), Service = "outside", TraceId = "t4", SpanId = "s4", Name = "old", DurationUs = 100000, Status = "error" }),
        }, harness.Token);
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "table.json");
        using var fixture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path, harness.Token));
        foreach (var item in fixture.RootElement.EnumerateArray())
        {
            var parameters = item.GetProperty("parameters").EnumerateObject().ToDictionary(property => property.Name,
                property => property.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? (object?)property.Value.GetDouble() : property.Value.GetString());
            var result = await harness.Executor.ExecuteSearchAsync(item.GetProperty("sql").GetString()!, now.AddMinutes(-5), now, 500, parameters, harness.Token);
            var name = item.GetProperty("name").GetString();
            Assert.DoesNotContain("ts_bucket", result.Columns);
            Assert.Equal(name == "errors" ? 2 : 1, result.Rows.Count);
            var first = result.Rows[0];
            Assert.Equal(name == "scalar" ? 3d : 2d, Convert.ToDouble(first[result.Columns.IndexOf("requests")]));
            Assert.Equal(1d, Convert.ToDouble(first[result.Columns.IndexOf("errors")]));
            Assert.Equal(name == "scalar" ? 100d / 3 : 50d, Convert.ToDouble(first[result.Columns.IndexOf("error %")]), 6);
            if (name == "slowest")
            {
                Assert.Equal("api", first[result.Columns.IndexOf("service")]);
                Assert.Equal(8600d, Convert.ToDouble(first[result.Columns.IndexOf("p95, \"latency\"")]), 6);
            }
        }
    }

    /// <summary>类别 Bar 跨天聚合后选 Top N，展示排序不改变入选类别，筛选值仅绑定.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_Bar_CrossDayTopN()
    {
        using var harness = new SearchHarness();
        var start = DateTimeOffset.UtcNow.Date.AddDays(-2);
        var from = new DateTimeOffset(start, TimeSpan.Zero);
        var malicious = "ERROR'; DROP TABLE logs; --";
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = from.AddHours(1), Service = "z", Severity = malicious, Body = "one" }),
            new(IngestKind.Log, new LogRecord { Timestamp = from.AddHours(25), Service = "z", Severity = malicious, Body = "two" }),
            new(IngestKind.Log, new LogRecord { Timestamp = from.AddHours(26), Service = "a", Severity = malicious, Body = "three" }),
            new(IngestKind.Log, new LogRecord { Timestamp = from.AddHours(27), Service = "b", Severity = malicious, Body = "four" }),
            new(IngestKind.Log, new LogRecord { Timestamp = from.AddHours(28), Service = "ignored", Severity = "INFO", Body = "other" }),
        }, harness.Token);
        const string sql = """
            WITH aggregated AS (
              SELECT service, count(*) AS value FROM {{logs}}
              WHERE ts >= $start AND ts < $end AND severity = $p0 GROUP BY service
            ), selected AS (
              SELECT * FROM aggregated ORDER BY value DESC NULLS LAST, service ASC NULLS LAST LIMIT $chart_limit
            ) SELECT * FROM selected ORDER BY service ASC NULLS LAST LIMIT 10001
            """;
        var result = await harness.Executor.ExecuteSearchAsync(sql, from, from.AddDays(2), 10000,
            new Dictionary<string, object?> { ["p0"] = malicious, ["chart_limit"] = 2 }, harness.Token);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("a", result.Rows[0][result.Columns.IndexOf("service")]);
        Assert.Equal("z", result.Rows[1][result.Columns.IndexOf("service")]);
        Assert.Equal(2L, Convert.ToInt64(result.Rows[1][result.Columns.IndexOf("value")]));
        Assert.DoesNotContain("ts_bucket", result.Columns);
        Assert.False(result.Truncated);
    }

    /// <summary>Number 使用前端同源夹具验证指标隔离、差值、Span 分位数及项目内会话去重.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_Number_SourceSemantics()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu'usage", Timestamp = now.AddMinutes(-4), Value = 10, LabelsJson = """{"host":"a"}""" }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu'usage", Timestamp = now.AddMinutes(-3), Value = 16, LabelsJson = """{"host":"a"}""" }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu'usage", Timestamp = now.AddMinutes(-4), Value = 100, LabelsJson = """{"host":"b"}""" }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu'usage", Timestamp = now.AddMinutes(-3), Value = 103, LabelsJson = """{"host":"b"}""" }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "other", Timestamp = now.AddMinutes(-1), Value = 9999 }),
            new(IngestKind.Trace, new SpanRecord { Timestamp = now.AddMinutes(-2), TraceId = "t", SpanId = "a", DurationUs = 1000 }),
            new(IngestKind.Trace, new SpanRecord { Timestamp = now.AddMinutes(-1), TraceId = "t", SpanId = "b", DurationUs = 9000 }),
            new(IngestKind.RumSession, new RumSessionRecord { ProjectKey = "a", SessionId = "same", Timestamp = now.AddMinutes(-4), Domain = "old.example" }),
            new(IngestKind.RumSession, new RumSessionRecord { ProjectKey = "a", SessionId = "same", Timestamp = now.AddMinutes(-1), Domain = "new.example" }),
            new(IngestKind.RumSession, new RumSessionRecord { ProjectKey = "b", SessionId = "same", Timestamp = now.AddMinutes(-2), Domain = "new.example" }),
        }, harness.Token);
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "number.json");
        using var fixture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path, harness.Token));
        foreach (var item in fixture.RootElement.EnumerateArray())
        {
            var parameters = item.GetProperty("parameters").EnumerateObject().ToDictionary(property => property.Name,
                property => property.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? (object?)property.Value.GetDouble() : property.Value.GetString());
            var result = await harness.Executor.ExecuteSearchAsync(item.GetProperty("sql").GetString()!, now.AddMinutes(-5), now, 100, parameters, harness.Token);
            var expected = item.GetProperty("name").GetString() switch
            {
                "metric" or "custom" => 57.25d,
                "delta" => 9d,
                "trace" => 8.92d,
                "session" => 2d,
                _ => throw new InvalidOperationException("未知 Number 夹具"),
            };
            Assert.Equal(expected, Convert.ToDouble(Assert.Single(result.Rows)[result.Columns.IndexOf("value")]), 6);
        }
    }

    /// <summary>Pie 同源 SQL 夹具验证四来源分类、指标隔离与项目内会话去重.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_Pie_SourceSemantics()
    {
        using var harness = new SearchHarness();
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-4), Service = "api", Body = "one" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now.AddMinutes(-1), Service = "api", Body = "two" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now, Service = "outside", Body = "end" }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu'usage", Timestamp = now.AddMinutes(-4), Value = 10, LabelsJson = """{"host,name":"a","env":"a'b"}""" }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu'usage", Timestamp = now.AddMinutes(-3), Value = 16, LabelsJson = """{"host,name":"a","env":"a'b"}""" }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu'usage", Timestamp = now.AddMinutes(-4), Value = 100, LabelsJson = """{"host,name":"b","env":"other"}""" }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu'usage", Timestamp = now.AddMinutes(-3), Value = 103, LabelsJson = """{"host,name":"b","env":"other"}""" }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "other", Timestamp = now.AddMinutes(-1), Value = 9999 }),
            new(IngestKind.Trace, new SpanRecord { Timestamp = now.AddMinutes(-2), Service = "api", TraceId = "t", SpanId = "a", DurationUs = 1000 }),
            new(IngestKind.Trace, new SpanRecord { Timestamp = now.AddMinutes(-1), Service = "api", TraceId = "t", SpanId = "b", DurationUs = 9000 }),
            new(IngestKind.RumSession, new RumSessionRecord { ProjectKey = "a", SessionId = "same", Timestamp = now.AddMinutes(-5), Domain = "old.example" }),
            new(IngestKind.RumSession, new RumSessionRecord { ProjectKey = "a", SessionId = "same", Timestamp = now.AddMinutes(-1), Domain = "new.example" }),
            new(IngestKind.RumSession, new RumSessionRecord { ProjectKey = "b", SessionId = "same", Timestamp = now.AddMinutes(-2), Domain = "new.example" }),
            new(IngestKind.RumSession, new RumSessionRecord { ProjectKey = "c", SessionId = "end", Timestamp = now, Domain = "outside" }),
        }, harness.Token);
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pie.json");
        using var fixture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path, harness.Token));
        foreach (var item in fixture.RootElement.EnumerateArray())
        {
            var parameters = item.GetProperty("parameters").EnumerateObject().ToDictionary(property => property.Name,
                property => property.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? (object?)property.Value.GetDouble() : property.Value.GetString());
            var result = await harness.Executor.ExecuteSearchAsync(item.GetProperty("sql").GetString()!, now.AddMinutes(-5), now, 100, parameters, harness.Token);
            var name = item.GetProperty("name").GetString();
            var expected = name switch
            {
                "logs" or "session" => new Dictionary<string, double> { [name == "logs" ? "api" : "new.example"] = 2d },
                "metric" => new Dictionary<string, double> { ["a"] = 13d },
                "delta" => new Dictionary<string, double> { ["a"] = 6d, ["b"] = 3d },
                "trace" => new Dictionary<string, double> { ["api"] = 8.92d },
                _ => throw new InvalidOperationException("未知 Pie 夹具"),
            };
            Assert.Equal(expected.Count, result.Rows.Count);
            Assert.Equal(2, result.Columns.Count);
            foreach (var row in result.Rows)
            {
                Assert.True(expected.TryGetValue(Convert.ToString(row[0])!, out var value));
                Assert.Equal(value, Convert.ToDouble(row[result.Columns.IndexOf("value")]), 6);
            }

            var samples = await harness.Executor.ExecuteSearchAsync(item.GetProperty("sampleSql").GetString()!, now.AddMinutes(-5), now, 500, parameters, harness.Token);
            Assert.NotEmpty(samples.Rows);
            if (name == "metric")
            {
                Assert.Equal(2, samples.Rows.Count);
                Assert.All(samples.Rows, row => Assert.Equal("cpu'usage", row[samples.Columns.IndexOf("name")]));
            }

            if (name == "session") Assert.Equal(2, samples.Rows.Count);
        }
    }

    /// <summary>服务地图同源查询验证跨天、去重、窗口边界与整 Trace 采样.</summary>
    [Fact]
    public async Task ExecuteSearchAsync_ServiceMap_TraceSamplingAndIdentity()
    {
        using var harness = new SearchHarness();
        var from = DateTimeOffset.Parse("2026-11-18T23:59:00Z");
        var to = DateTimeOffset.Parse("2026-11-19T00:01:00Z");
        var items = new List<IngestItem>();
        for (var i = 0; i < 40; i++)
        {
            var traceId = "trace-" + i;
            items.Add(new(IngestKind.Trace, new SpanRecord { Timestamp = from.AddSeconds(30), TraceId = traceId, SpanId = "root", Service = "api", Name = "entry", DurationUs = 1000 }));
            var child = new SpanRecord { Timestamp = from.AddSeconds(70), TraceId = traceId, SpanId = "child", ParentSpanId = "root", Service = "db", Name = "query", DurationUs = 100, Status = "error" };
            items.Add(new(IngestKind.Trace, child));
            items.Add(new(IngestKind.Trace, child));
            items.Add(new(IngestKind.Trace, new SpanRecord { Timestamp = from.AddSeconds(80), TraceId = traceId, SpanId = "orphan", ParentSpanId = "missing", Service = "worker", Name = "orphan" }));
            items.Add(new(IngestKind.Trace, new SpanRecord { Timestamp = to, TraceId = traceId, SpanId = "excluded", ParentSpanId = "root", Service = "outside" }));
        }

        items.Add(new(IngestKind.Trace, new SpanRecord { Timestamp = from.AddSeconds(20), TraceId = "unmatched", SpanId = "root", Service = "unrelated" }));
        await harness.Writer.FlushAsync(items, harness.Token);
        using var fixture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "service-map.json"), harness.Token));
        foreach (var item in fixture.RootElement.EnumerateArray())
        {
            var request = item.GetProperty("request");
            var parameters = request.GetProperty("parameters").EnumerateObject().ToDictionary(property => property.Name,
                property => property.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Number => (object?)property.Value.GetDouble(),
                    System.Text.Json.JsonValueKind.Null => null,
                    _ => property.Value.GetString(),
                });
            var result = await harness.Executor.ExecuteSearchAsync(request.GetProperty("sql").GetString()!, from, to, 500, parameters, harness.Token);
            object? Value(IReadOnlyList<object?> row, string name) => row[result.Columns.IndexOf(name)];
            if (item.GetProperty("name").GetString() == "examples")
            {
                Assert.Equal(21, result.Rows.Count);
                Assert.All(result.Rows, row => Assert.Equal(1L, Convert.ToInt64(Value(row, "calls"))));
                continue;
            }

            var meta = Assert.Single(result.Rows.Where(row => Equals(Value(row, "type"), "meta")));
            var traces = Convert.ToInt64(Value(meta, "traces"));
            Assert.Equal(40L, Convert.ToInt64(Value(meta, "last_seen")));
            Assert.Equal(traces, Convert.ToInt64(Value(meta, "errors")));
            Assert.Equal(traces, Convert.ToInt64(Value(meta, "first_seen")));
            Assert.Equal(3L, Convert.ToInt64(Value(meta, "calls")));
            var edge = Assert.Single(result.Rows.Where(row => Equals(Value(row, "type"), "edge")));
            Assert.Equal("api", Value(edge, "source"));
            Assert.Equal("db", Value(edge, "target"));
            Assert.Equal(traces, Convert.ToInt64(Value(edge, "calls")));
            Assert.Equal(traces, Convert.ToInt64(Value(edge, "errors")));
            Assert.Equal(3, result.Rows.Count(row => Equals(Value(row, "type"), "node")));
            if (item.GetProperty("name").GetString() == "map")
            {
                Assert.Equal(40L, traces);
            }
            else
            {
                Assert.InRange(traces, 1L, 39L);
                var repeat = await harness.Executor.ExecuteSearchAsync(request.GetProperty("sql").GetString()!, from, to, 500, parameters, harness.Token);
                Assert.Equal(traces, Convert.ToInt64(Value(Assert.Single(repeat.Rows.Where(row => Equals(Value(row, "type"), "meta"))), "traces")));
            }
        }
    }

    [Fact]
    public async Task ResultLimit_IsAppliedBeforeMaterializingWideResults()
    {
        using var harness = new SearchHarness(options => options.DuckDb.MemoryLimit = "128MB");
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var result = await harness.Executor.ExecuteAsync("SELECT i, repeat(md5(i::VARCHAR), 32) AS payload FROM range(500000) r(i)", 2);
            Assert.Equal(2, result.Rows.Count);
            Assert.True(result.Truncated);
            Assert.Equal(1024, Assert.IsType<string>(result.Rows[0][1]).Length);
            Assert.Single((await harness.Executor.ExecuteAsync("SELECT 42")).Rows);
        }
    }

    [Fact]
    public async Task ResultLimit_PreservesCteOrderingAndInternalParameterCollision()
    {
        using var harness = new SearchHarness();
        var result = await harness.Executor.ExecuteSearchAsync(
            "WITH rows AS (SELECT i FROM range(10) r(i)) SELECT i + $sdata_result_limit AS value FROM rows ORDER BY i DESC -- trailing note",
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, 3,
            new Dictionary<string, object?> { ["sdata_result_limit"] = 100 });
        Assert.Equal(new long[] { 109, 108, 107 }, result.Rows.Select(row => Convert.ToInt64(row[0])));
        Assert.True(result.Truncated);
        var exact = await harness.Executor.ExecuteAsync("SELECT i FROM range(3) r(i)", 3);
        Assert.False(exact.Truncated);
    }

    [Fact]
    public async Task QueryBudget_InterruptsNativeWork_AndReleasesPermit()
    {
        using var harness = new SearchHarness(options => options.DuckDb.QueryTimeoutSeconds = 1);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<BusinessException>(() => harness.Executor.ExecuteAsync(
            "SELECT sum(sin(a.range + b.range)) FROM range(1000000) a CROSS JOIN range(1000000) b").WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(408, failure.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Single((await harness.Executor.ExecuteAsync("SELECT 42").WaitAsync(TimeSpan.FromSeconds(5))).Rows);
    }

    [Fact]
    public async Task QueryBudget_IncludesAdmissionWait_AndHonorsCallerCancellation()
    {
        using var harness = new SearchHarness(options => options.DuckDb.QueryTimeoutSeconds = 1);
        using var lease = await harness.Manager.LockQueryAsync(CancellationToken.None);
        var failure = await Assert.ThrowsAsync<BusinessException>(() => harness.Executor.ExecuteAsync("SELECT 42"));
        Assert.Equal(408, failure.StatusCode);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Executor.ExecuteAsync("SELECT 42", cancellationToken: stop.Token));
        lease.Dispose();
        Assert.Single((await harness.Executor.ExecuteAsync("SELECT 42")).Rows);
    }

    [Fact]
    public async Task AlertCount_HalfOpenWindow_CapsAndBinds()
    {
        using var harness = new SearchHarness();
        var end = DateTimeOffset.UtcNow;
        var start = end.AddMinutes(-5);
        await harness.Writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = start, Service = "api", Severity = "ERROR", Body = "a" }),
            new(IngestKind.Log, new LogRecord { Timestamp = start.AddSeconds(1), Service = "api", Severity = "ERROR", Body = "b" }),
            new(IngestKind.Log, new LogRecord { Timestamp = end, Service = "api", Severity = "ERROR", Body = "excluded" }),
        }, harness.Token);
        var parameters = new Dictionary<string, object?> { ["p0"] = "api" };
        var count = await harness.Executor.ExecuteAlertCountAsync(new AlertCountQuery("logs", "service = $p0", parameters, start, end, 10));
        Assert.Equal(2, count.Count);
        Assert.False(count.LowerBound);
        var capped = await harness.Executor.ExecuteAlertCountAsync(new AlertCountQuery("logs", "service = $p0", parameters, start, end, 1));
        Assert.Equal(1, capped.Count);
        Assert.True(capped.LowerBound);
    }

    [Fact]
    public async Task AlertCount_EmptyDataIsZero_AndOldWindowIsRejected()
    {
        using var harness = new SearchHarness();
        var end = DateTimeOffset.UtcNow;
        var empty = await harness.Executor.ExecuteAlertCountAsync(new AlertCountQuery("logs", "", new Dictionary<string, object?>(), end.AddMinutes(-5), end, 2));
        Assert.Equal(0, empty.Count);
        await Assert.ThrowsAsync<BusinessException>(() => harness.Executor.ExecuteAlertCountAsync(new AlertCountQuery("logs", "", new Dictionary<string, object?>(), end.AddDays(-90).AddMinutes(-5), end.AddDays(-90), 2)));
    }

    /// <summary>执行测试夹具：临时库 + 缓冲 + 写入器 + 搜索执行器.</summary>
    private sealed class SearchHarness : IDisposable
    {
        /// <summary>临时目录.</summary>
        public readonly string TempDirectory;

        /// <summary>连接管理器.</summary>
        public readonly DuckDbConnectionManager Manager;

        /// <summary>批量写入器.</summary>
        public readonly IngestWriterService Writer;

        /// <summary>搜索执行器.</summary>
        public readonly AnalyticsExecutor Executor;

        /// <summary>表解析器.</summary>
        public readonly TableResolver Resolver;

        /// <summary>台账内存镜像.</summary>
        public readonly ColdLedgerCache LedgerCache;

        /// <summary>SQLite 台账上下文.</summary>
        public readonly DatabaseContext Ledger;

        /// <summary>取消令牌.</summary>
        public readonly CancellationToken Token = CancellationToken.None;

        /// <summary>
        /// Initializes a new instance of the <see cref="SearchHarness"/> class.
        /// </summary>
        /// <param name="configure">配置回调.</param>
        public SearchHarness(Action<SystemOptions>? configure = null)
        {
            TempDirectory = Path.Combine(Path.GetTempPath(), "sdata-searchsql-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDirectory);
            AppConst.ContentRoot = TempDirectory;

            var options = new SystemOptions();
            options.DuckDb.Path = Path.Combine(TempDirectory, "searchsql-test.duckdb");
            options.DuckDb.Threads = 2;
            configure?.Invoke(options);

            Manager = new DuckDbConnectionManager(options, NullLogger<DuckDbConnectionManager>.Instance);
            Manager.InitializeAsync().GetAwaiter().GetResult();
            Writer = new IngestWriterService(new IngestBuffer(options), Manager, options, new SData.Infra.Services.DefaultIdProvider(1), NullLogger<IngestWriterService>.Instance);

            var sqlitePath = Path.Combine(TempDirectory, "ledger.db");
            var dbOptions = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={sqlitePath}").Options;
            Ledger = new DatabaseContext(dbOptions);
            Ledger.Database.EnsureCreated();
            var scopeFactory = new SingleScopeFactory(Ledger);
            var settings = new SystemSettingsProvider(options, scopeFactory, new SData.Infra.Services.DefaultIdProvider(1), NullLogger<SystemSettingsProvider>.Instance);
            var objectStore = new CompositeObjectStore(new LocalObjectStore(), new S3ObjectStore(options, NullLogger<S3ObjectStore>.Instance));
            var coldCache = new ColdCacheService(options, objectStore, NullLogger<ColdCacheService>.Instance);
            var ledgerCache = new ColdLedgerCache(scopeFactory, NullLogger<ColdLedgerCache>.Instance);
            LedgerCache = ledgerCache;
            Resolver = new TableResolver(Manager, options, settings, ledgerCache, coldCache, NullLogger<TableResolver>.Instance);
            Executor = new AnalyticsExecutor(Manager, options, Resolver, NullLogger<AnalyticsExecutor>.Instance);
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

    /// <summary>单例 scope 工厂（台账上下文直供，与 ArchiveTests 同构）.</summary>
    private sealed class SingleScopeFactory : IServiceScopeFactory
    {
        private readonly DatabaseContext _dbContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="SingleScopeFactory"/> class.
        /// </summary>
        /// <param name="dbContext">上下文.</param>
        public SingleScopeFactory(DatabaseContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <inheritdoc/>
        public IServiceScope CreateScope()
        {
            return new SingleScope(_dbContext);
        }

        private sealed class SingleScope : IServiceScope
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="SingleScope"/> class.
            /// </summary>
            /// <param name="dbContext">上下文.</param>
            public SingleScope(DatabaseContext dbContext)
            {
                ServiceProvider = new SingleProvider(dbContext);
            }

            /// <inheritdoc/>
            public IServiceProvider ServiceProvider { get; }

            /// <inheritdoc/>
            public void Dispose()
            {
                // 单例上下文由夹具统一释放
            }
        }

        private sealed class SingleProvider : IServiceProvider
        {
            private readonly DatabaseContext _dbContext;

            /// <summary>
            /// Initializes a new instance of the <see cref="SingleProvider"/> class.
            /// </summary>
            /// <param name="dbContext">上下文.</param>
            public SingleProvider(DatabaseContext dbContext)
            {
                _dbContext = dbContext;
            }

            /// <inheritdoc/>
            public object? GetService(Type serviceType)
            {
                return serviceType == typeof(DatabaseContext) ? _dbContext : null;
            }
        }
    }
}
