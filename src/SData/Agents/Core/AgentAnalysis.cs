using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.Logging;
using SData.Infra.Configuration;
using SData.Infra.Exceptions;
using SData.Store;

namespace SData.Agents;

public partial class AgentAnalysis(IAnalyticsExecutor executor, IAnalyticsCatalog catalog, ILogger<AgentAnalysis> logger, SData.Settings.ISystemSettingsProvider settings, SystemOptions? system = null) : IAgentAnalysis
{
    public async Task<AgentResult> ExecuteAsync(string tool, AgentRequest query, CancellationToken cancellationToken)
    {
        Validate(query);
        var to = query.To ?? DateTimeOffset.UtcNow;
        var from = query.From ?? to.AddMinutes(-15);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.GetRuntime().Agents.QueryTimeoutSeconds, 1, 10)));
        var watch = Stopwatch.StartNew();
        try
        {
            var result = tool switch
            {
                "list_data_sources" => new AgentResult { Metadata = AgentDataCatalog.Sources.Select(AgentDataCatalog.Describe).ToArray(), Semantics = "Logical sources, including absent/empty sources; use list_tables for actual hot tables." },
                "get_data_organization" => Organization(),
                "list_tables" or "describe_table" => await Tables(tool, (AgentTableQuery)query, from, to, deadline.Token),
                "query_data" or "aggregate_data" => await Data((AgentDataQuery)query, tool == "aggregate_data", from, to, deadline.Token),
                "get_field_values" => await FieldValues((AgentFieldQuery)query, from, to, deadline.Token),
                "compare_periods" => await Compare((AgentCompareQuery)query, from, to, deadline.Token),
                "query_metric_series" or "query_iot_series" => await Series((AgentSeriesQuery)query, tool == "query_iot_series", from, to, deadline.Token),
                _ => await Summary(tool, query, from, to, deadline.Token),
            };
            result.From = from; result.To = to;
            result.ElapsedMs = watch.ElapsedMilliseconds;
            Bound(result);
            logger.LogInformation("Agent 分析工具 {Tool} 完成 {ElapsedMs}ms", tool, watch.ElapsedMilliseconds);
            return result;
        }
        catch (BusinessException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            logger.LogWarning("Agent 分析工具 {Tool} 失败 {ElapsedMs}ms", tool, watch.ElapsedMilliseconds);
            throw BusinessException.Throw(503, "查询未完成，请缩小范围或检查服务状态");
        }
    }

    private static void Validate(AgentRequest query)
    {
        if (query == null) throw BusinessException.Throw(400, "缺少查询参数");
        bool Check<T>(T value, Action<AbstractValidator<T>> rules)
        {
            var validator = new InlineValidator<T>(); rules(validator);
            return validator.Validate(value).IsValid;
        }
        var valid = query switch
        {
            AgentCompareQuery q => Check(q, AgentCompareQuery.Validate),
            AgentFieldQuery q => Check(q, AgentFieldQuery.Validate),
            AgentDataQuery q => Check(q, AgentDataQuery.Validate),
            AgentTableQuery q => Check(q, AgentTableQuery.Validate),
            AgentSeriesQuery q => Check(q, AgentSeriesQuery.Validate),
            AgentRumQuery q => Check(q, AgentRumQuery.Validate),
            _ => Check(query, AgentRequest.Validate),
        };
        if (!valid) throw BusinessException.Throw(400, "查询参数无效：时间窗口最多24小时，结果最多200条，请检查工具参数");
    }

    private async Task<AgentResult> Run(string sql, Dictionary<string, object?> parameters, List<string> columns, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken token, string semantics = "")
    {
        var data = await executor.ExecuteSearchAsync(sql, from, to, limit + 1, parameters, token);
        return new AgentResult
        {
            Columns = columns.Count > 0 ? columns : data.Columns,
            Rows = data.Rows.Take(limit).ToList(),
            Truncated = data.Truncated || data.Rows.Count > limit,
            From = from, To = to, Semantics = semantics,
        };
    }

    private Task<AgentResult> Data(AgentDataQuery query, bool aggregate, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        var compiled = AgentSqlCompiler.Compile(query, aggregate);
        return Run(compiled.Sql, compiled.Parameters, compiled.Columns, from, to, query.Limit, token,
            aggregate ? "Exact aggregates over the requested window, followed by limited groups; not an exhaustive group list when truncated. Numeric units match source fields." : "Bounded safe projection, newest first by default. Text may be shortened to 2048 characters.");
    }

    private Task<AgentResult> FieldValues(AgentFieldQuery query, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        if (query.Fields.Count > 0 || query.GroupBy.Count > 0 || query.Aggregations.Count > 0 || query.StepSeconds.HasValue || query.Sort.Count > 0)
            throw BusinessException.Throw(400, "字段值发现仅接受数据源、字段、过滤与时间范围");
        var source = AgentDataCatalog.Source(query.Domain);
        var field = AgentDataCatalog.Field(source, query.Field);
        if (field.Name is "body" or "str_value" or "page_path" || field.Type == "TIMESTAMP")
            throw BusinessException.Throw(400, "该字段不支持值枚举");
        return Data(new AgentDataQuery { Domain = query.Domain, Project = query.Project, Filters = query.Filters, Limit = query.Limit, GroupBy = [field.Name], Aggregations = [new() { Alias = "records" }] }, true, from, to, token);
    }

    private async Task<AgentResult> Compare(AgentCompareQuery query, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        if (to - from != query.BaselineTo - query.BaselineFrom || query.GroupBy.Count > 0 || query.StepSeconds.HasValue || query.Sort.Count > 0)
            throw BusinessException.Throw(400, "时间对比要求等长窗口，不接受分组、时间桶或排序");
        var definitions = query.Aggregations.Count == 0 ? [new AgentAggregation()] : query.Aggregations;
        if (definitions.Any(a => a.Function is "min" or "max" && AgentDataCatalog.Field(AgentDataCatalog.Source(query.Domain), a.Field ?? "").Numeric == false))
            throw BusinessException.Throw(400, "时间对比仅支持数值统计");
        var current = await Data(query, true, from, to, token);
        var baseline = await Data(query, true, query.BaselineFrom, query.BaselineTo, token);
        var rows = new List<List<object?>>();
        for (var i = 0; i < current.Columns.Count; i++)
        {
            double? Number(AgentResult r) => r.Rows.Count == 0 || r.Rows[0][i] == null ? null : Convert.ToDouble(r.Rows[0][i], System.Globalization.CultureInfo.InvariantCulture);
            var c = Number(current); var b = Number(baseline);
            var delta = c - b;
            rows.Add([current.Columns[i], c, b, delta, b == null || b == 0 ? null : delta / Math.Abs(b.Value) * 100]);
        }
        return new AgentResult { Columns = ["statistic", "current", "baseline", "delta", "change_percent"], Rows = rows,
            Sections = new() { ["current"] = current, ["baseline"] = baseline },
            Semantics = "Equal-duration exact window aggregates; change_percent is null for zero/missing baseline. No causal inference." };
    }

    private AgentResult Organization() => new()
    {
        Metadata = new
        {
            storage = new { observability = "DuckDB; bounded ingestion buffer and batch appender", metadata = "SQLite; excluded from MCP queries" },
            time = "UTC TIMESTAMP (microsecond precision), [from,to), physical day suffix yyyyMMdd",
            organization = "Logical sources resolve hot day tables, unpublished internal batches and published cold Parquet. Physical hot-table catalog does not enumerate cold history.",
            correlations = new[] { "logs.trace_id + span_id -> traces.trace_id + span_id", "traces.parent_span_id within trace_id -> parent span", "metrics.name + safe labels identify a series", "iot.device_key + signal identify a signal", "RUM project_key + session_id correlate page reports and events; session is not a user" },
            retention = Retention(),
            limitations = new[] { "No arbitrary SQL, writes, raw JSON, replay, credentials, system settings, storage paths or administrator access.", "Agent keys currently allow global observability reading; project/service parameters are filters, not authorization scopes.", "Cold availability/completeness is unknown; no rows does not prove no historical data.", "Each data window <=24h, default 15m; <=200 rows/groups, <=2000 series points, <=20 series, <=10s per tool call.", "Directory fields describe physical schema; Readable=false fields cannot be queried by MCP." },
            workflow = new[] { "list_data_sources", "get_data_schema / list_tables / describe_table", "get_field_values", "query_data / aggregate_data", "compare_periods / domain summaries", "drill down with trace_id, metric labels, device/signal or RUM project" },
        },
        Semantics = "Storage and query organization, not sensitive runtime locations.",
    };

    private object Retention()
    {
        var plan = settings.GetRetention();
        var legacy = system?.DuckDb.Retention ?? new RetentionOptions();
        object Tier(RetentionTierOptions tier, int fallback) => new { hotDays = tier.HotDays > 0 ? tier.HotDays : fallback, coldDays = tier.ColdDays };
        return new
        {
            logs = Tier(plan.Logs, legacy.Logs), traces = Tier(plan.Traces, legacy.Traces),
            metrics = Tier(plan.Metrics, legacy.Metrics), iot = Tier(plan.Iot, legacy.Iot), rum = Tier(plan.Rum, 0),
            meaning = "Effective retention policy, not a guarantee of available history. hotDays<=0 disables expiry/archive; coldDays=0 keeps cold data indefinitely.",
        };
    }

    private async Task<AgentResult> Tables(string tool, AgentTableQuery query, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        if (query.Domain != null) AgentDataCatalog.Source(query.Domain);
        var tables = (await catalog.GetTablesAsync(token)).Where(t => t.Schema == "main" && AgentDataCatalog.Sources.Any(s => t.Name.Length == s.Prefix.Length + 8 && t.Name.StartsWith(s.Prefix, StringComparison.Ordinal) && DateTimeOffset.TryParseExact(t.Name[s.Prefix.Length..], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out _)))
            .Where(t => query.Domain == null || t.Name.StartsWith(AgentDataCatalog.Source(query.Domain).Prefix, StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        foreach (var table in tables)
            table.Day = DateTimeOffset.ParseExact(table.Name[^8..], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
        if (tool == "describe_table")
        {
            var table = tables.SingleOrDefault(t => t.Name == query.Table) ?? throw BusinessException.Throw(400, "表不存在或不允许访问，请先调用 list_tables");
            var source = AgentDataCatalog.Sources.Single(s => table.Name.StartsWith(s.Prefix, StringComparison.Ordinal));
            return new AgentResult { Metadata = new { table.Schema, table.Name, source.Domain, table.Day,
                columns = table.Columns.Select(c => new { c.Name, c.Type, c.Nullable, readable = source.Fields.Any(f => f.Name == c.Name && f.Readable), meaning = source.Fields.FirstOrDefault(f => f.Name == c.Name)?.Meaning ?? "Unrecognized physical field; not queryable", unit = source.Fields.FirstOrDefault(f => f.Name == c.Name)?.Unit }),
                logicalSchema = AgentDataCatalog.Describe(source) }, Semantics = "Actual hot-table schema; readable=false physical columns cannot be queried. Use the logical domain, not this table name, in query_data." };
        }
        if (query.From.HasValue || query.To.HasValue) tables = tables.Where(t => t.Day >= new DateTimeOffset(from.UtcDateTime.Date, TimeSpan.Zero) && t.Day < to).ToList();
        var page = tables.Skip(query.Offset).Take(query.Limit).ToList();
        return new AgentResult { Columns = ["schema", "table", "domain", "utc_day"], Rows = page.Select(t => new List<object?> { t.Schema, t.Name, AgentDataCatalog.Sources.Single(s => t.Name.StartsWith(s.Prefix, StringComparison.Ordinal)).Domain, t.Day }).ToList(),
            Truncated = query.Offset + page.Count < tables.Count, Metadata = new { nextOffset = query.Offset + page.Count < tables.Count ? (int?)(query.Offset + page.Count) : null, hotTablesOnly = true },
            Semantics = "Actual allowed hot day tables only, sorted by name. No business-row scan. Cold history is queried through logical sources, not enumerated here. Catalog may change between pages." };
    }

    private static void Bound(AgentResult result)
    {
        void Scrub(AgentResult part)
        {
            foreach (var row in part.Rows)
                for (var i = 0; i < row.Count; i++)
                    if (row[i] is string value)
                    {
                        if (value.Length > 2048) { part.FieldTruncated = true; value = value[..2048]; }
                        row[i] = AgentQueries.Redact(value);
                    }
            foreach (var section in part.Sections.Values) { Scrub(section); part.FieldTruncated |= section.FieldTruncated; part.Truncated |= section.Truncated; }
        }
        Scrub(result);
        // Budget the complete service payload (including metadata/sections), leaving room for MCP wrapping.
        while (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result)) > 32 * 1024)
        {
            result.Truncated = true;
            var parts = result.Sections.Values.Append(result).Where(x => x.Rows.Count > 0).OrderByDescending(x => x.Rows.Count).ToList();
            if (parts.Count > 0) { parts[0].Rows.RemoveAt(parts[0].Rows.Count - 1); continue; }
            result.Metadata = null;
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result)) > 32 * 1024) throw BusinessException.Throw(503, "结果结构超过输出预算，请缩小查询");
        }
    }
}
