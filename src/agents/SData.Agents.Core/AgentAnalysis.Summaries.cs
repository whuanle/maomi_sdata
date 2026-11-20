using System.Text.Json;
using SData.Infra.Exceptions;

namespace SData.Agents;

public partial class AgentAnalysis
{
    private async Task<AgentResult> Summary(string tool, AgentRequest request, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        var limit = request.Limit;
        var parameters = new Dictionary<string, object?> { ["cap"] = limit + 1 };
        Task<AgentResult> Fixed(string sql, string semantics) => Run(sql, parameters, [], from, to, limit, token, semantics);
        switch (tool)
        {
            case "list_services":
                var logs = await Fixed("SELECT left(service,2049) AS service,count(*) AS records FROM {{logs}} WHERE ts >= $start AND ts < $end GROUP BY service ORDER BY records DESC LIMIT $cap", "Services observed in logs; bounded list.");
                var spans = await Fixed("SELECT left(service,2049) AS service,count(*) AS spans FROM {{traces}} WHERE ts >= $start AND ts < $end GROUP BY service ORDER BY spans DESC LIMIT $cap", "Services observed in spans; bounded list.");
                logs.Sections["traces"] = spans;
                logs.Semantics = "Top-level rows retain legacy log service counts; traces section additionally discovers services seen only in spans. Each list is independently limited; counts are not requests.";
                return logs;
            case "summarize_logs":
                return await Data(new AgentDataQuery { Domain = "logs", Limit = limit, GroupBy = ["service", "severity"], Aggregations = [new() { Alias = "records" }], StepSeconds = 60 }, true, from, to, token);
            case "summarize_traces":
                return await Data(new AgentDataQuery { Domain = "traces", Limit = limit, GroupBy = ["service", "status"], Aggregations = [
                    new() { Alias = "spans" }, new() { Function = "distinct_count", Field = "trace_id", Alias = "traces" },
                    new() { Function = "avg", Field = "duration_us", Alias = "avg_us" },
                    new() { Function = "quantile", Field = "duration_us", Alias = "p95_us", Quantile = 0.95 }
                ] }, true, from, to, token);
            case "get_service_dependencies":
                return await Fixed("WITH spans AS (SELECT DISTINCT trace_id,span_id,parent_span_id,service,duration_us FROM {{traces}} WHERE ts >= $start AND ts < $end), parents AS (SELECT trace_id,span_id,min(service) AS service FROM spans GROUP BY trace_id,span_id HAVING count(DISTINCT service)=1), children AS (SELECT trace_id,span_id,min(parent_span_id) AS parent_span_id,min(service) AS service,max(duration_us) AS duration_us FROM spans GROUP BY trace_id,span_id HAVING count(DISTINCT service)=1 AND count(DISTINCT parent_span_id)=1) SELECT left(p.service,2049) AS parent_service,left(c.service,2049) AS child_service,count(*) AS child_spans,avg(c.duration_us) AS avg_child_us FROM children c JOIN parents p ON c.trace_id=p.trace_id AND c.parent_span_id=p.span_id WHERE p.service<>c.service GROUP BY p.service,c.service ORDER BY child_spans DESC LIMIT $cap",
                    "Observed cross-service parent/child spans only. Deduplicated by trace_id/span_id; ambiguous identities excluded. Missing parents outside the window are not inferred. Counts are child spans, not requests; no causal conclusion.");
            case "list_devices":
                return await Data(new AgentDataQuery { Domain = "iot", Limit = limit, GroupBy = ["device_key"], Aggregations = [new() { Alias = "samples" }] }, true, from, to, token);
            case "list_device_signals":
                var device = ((AgentSeriesQuery)request).Device;
                if (string.IsNullOrWhiteSpace(device)) throw BusinessException.Throw(400, "缺少 device");
                parameters["device"] = device;
                return await Fixed("SELECT left(signal,2049) AS signal,count(*) AS samples,count(num_value) AS numeric_samples,min(ts) AS first_seen,max(ts) AS last_seen FROM {{iot}} WHERE ts >= $start AND ts < $end AND device_key=$device GROUP BY signal ORDER BY samples DESC LIMIT $cap", "Signals observed for this device in the window; no credential metadata.");
            case "list_metric_labels":
                return await MetricLabels((AgentSeriesQuery)request, from, to, token);
            case "list_rum_projects":
                // Query independently so an empty event or session family does not suppress the other.
                var sessions = await Fixed("SELECT left(project_key,2049) AS project_key,count(*) AS page_reports FROM {{sessions}} WHERE ts >= $start AND ts < $end GROUP BY project_key ORDER BY page_reports DESC LIMIT $cap", "Observed page-report projects.");
                var events = await Fixed("SELECT left(project_key,2049) AS project_key,count(*) AS events FROM {{rum_events}} WHERE ts >= $start AND ts < $end GROUP BY project_key ORDER BY events DESC LIMIT $cap", "Observed event projects.");
                return new AgentResult { Sections = new() { ["page_reports"] = sessions, ["events"] = events }, Semantics = "Project identifiers from telemetry only, not project configuration or authorization scopes. Each section is independently limited." };
            case "summarize_rum":
            case "summarize_rum_errors":
                var project = ((AgentRumQuery)request).Project;
                parameters["project"] = project;
                if (tool == "summarize_rum_errors")
                    return await Fixed("SELECT left(event_name,2049) AS error_name,left(page_path,2049) AS page_path,count(*) AS errors,count(DISTINCT session_id) AS sessions FROM {{rum_events}} WHERE ts >= $start AND ts < $end AND project_key=$project AND event_type='error' GROUP BY event_name,page_path ORDER BY errors DESC LIMIT $cap", "Top error names/pages, not total errors or root causes. No raw payload.");
                var reports = await Fixed("SELECT count(*) AS page_reports,count(DISTINCT session_id) AS sessions FROM {{sessions}} WHERE ts >= $start AND ts < $end AND project_key=$project", "Page reports and distinct sessions; sessions are not unique users.");
                var vitals = await Fixed("SELECT left(event_name,2049) AS vital,count(value) AS samples,quantile_cont(value,0.75) AS p75 FROM {{rum_events}} WHERE ts >= $start AND ts < $end AND project_key=$project AND event_type='vital' AND value IS NOT NULL GROUP BY event_name ORDER BY samples DESC LIMIT $cap", "Vitals p75; LCP/INP/FCP/TTFB in milliseconds, CLS unitless; other units instrument-defined.");
                var types = await Fixed("SELECT left(event_type,2049) AS event_type,count(*) AS events FROM {{rum_events}} WHERE ts >= $start AND ts < $end AND project_key=$project GROUP BY event_type ORDER BY events DESC LIMIT $cap", "Event category counts.");
                return new AgentResult { Sections = new() { ["page_reports"] = reports, ["vitals"] = vitals, ["events"] = types }, Semantics = "Exact project filter across independent RUM families; no replay or raw payload. Empty sections do not prove complete history." };
            default:
                throw BusinessException.Throw(400, "未知工具");
        }
    }

    private static string Label(string key)
    {
        if (!AgentDataCatalog.LabelKeys.Contains(key, StringComparer.Ordinal)) throw BusinessException.Throw(400, "标签不在安全维度清单中");
        // key is selected from internal constants, never arbitrary JSON paths.
        return "json_extract_string(try_cast(labels AS JSON), '$.\"" + key + "\"')";
    }

    private Task<AgentResult> MetricLabels(AgentSeriesQuery query, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query.Metric)) throw BusinessException.Throw(400, "缺少指标名称");
        if (query.Labels.Count > 0 || query.SplitByLabels || query.Device != null || query.Signal != null)
            throw BusinessException.Throw(400, "标签发现仅接受 metric 和时间范围");
        var parameters = new Dictionary<string, object?> { ["metric"] = query.Metric, ["cap"] = query.Limit + 1 };
        var parts = AgentDataCatalog.LabelKeys.Select((key, index) =>
        {
            parameters["key" + index] = key;
            return "SELECT $key" + index + " AS label," + Label(key) + " AS value FROM samples";
        });
        return Run("WITH samples AS (SELECT labels FROM {{metrics}} WHERE ts >= $start AND ts < $end AND name=$metric), dimensions AS (" + string.Join(" UNION ALL ", parts) + ") SELECT label,left(value,2049) AS value,count(*) AS samples FROM dimensions WHERE value IS NOT NULL GROUP BY label,value ORDER BY samples DESC LIMIT $cap",
            parameters, ["label", "value", "samples"], from, to, query.Limit, token, "Top safe label/value pairs only, not all labels. No raw label JSON; free-text label values remain untrusted.");
    }

    private async Task<AgentResult> Series(AgentSeriesQuery query, bool iot, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        if (iot ? string.IsNullOrWhiteSpace(query.Device) || string.IsNullOrWhiteSpace(query.Signal) : string.IsNullOrWhiteSpace(query.Metric))
            throw BusinessException.Throw(400, iot ? "缺少 device/signal" : "缺少指标名称");
        if (iot && (query.Labels.Count > 0 || query.SplitByLabels || query.Metric != null) || !iot && (query.Device != null || query.Signal != null))
            throw BusinessException.Throw(400, "指标与物联网参数不能混用");
        var parameters = new Dictionary<string, object?> { ["step"] = query.StepSeconds, ["metric"] = query.Metric, ["device"] = query.Device, ["signal"] = query.Signal };
        var value = iot ? "num_value" : "value";
        var aggregate = query.Aggregation switch
        {
            "avg" => "avg(" + value + ")", "sum" => "sum(" + value + ")", "min" => "min(" + value + ")", "max" => "max(" + value + ")",
            "count" => "count(" + value + ")", "p95" => "quantile_cont(" + value + ",0.95)",
            _ => throw BusinessException.Throw(400, "不支持的时序聚合函数"),
        };
        var where = "ts >= $start AND ts < $end AND " + (iot ? "device_key=$device AND signal=$signal" : "name=$metric");
        foreach (var pair in query.Labels)
        {
            var name = "label" + parameters.Count;
            parameters[name] = pair.Value;
            where += " AND " + Label(pair.Key) + "=$" + name;
        }
        var dimensions = !iot && query.SplitByLabels
            ? AgentDataCatalog.LabelKeys.Select((key, index) => Label(key) + " AS d" + index).ToList() : [];
        var aliases = Enumerable.Range(0, dimensions.Count).Select(i => "d" + i).ToList();
        var projection = dimensions.Count == 0 ? "" : "," + string.Join(",", dimensions);
        var group = dimensions.Count == 0 ? "" : "," + string.Join(",", aliases);
        var sql = "SELECT to_timestamp(floor(epoch(ts)/$step)*$step) AS ts," + aggregate + " AS value" + projection + " FROM {{" + (iot ? "iot" : "metrics") + "}} WHERE " + where + " GROUP BY 1" + group + " ORDER BY 1" + group + " LIMIT 2001";
        var result = await Run(sql, parameters, new[] { "ts", "value" }.Concat(aliases.Select((_, i) => AgentDataCatalog.LabelKeys[i])).ToList(), from, to, 2000, token,
            iot ? "Numeric signal buckets; count counts numeric samples. Quality counts are a separate section." : query.SplitByLabels ? "Series split only by safe label dimensions; other labels collapse. No counter rate, interpolation or zero fill." : "All labels collapse into one series (legacy default); no counter rate, interpolation or zero fill.");
        if (dimensions.Count > 0)
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            result.Rows = result.Rows.Where(row =>
            {
                var id = JsonSerializer.Serialize(row.Skip(2));
                if (identities.Contains(id)) return true;
                if (identities.Count >= 20) { result.Truncated = true; return false; }
                identities.Add(id); return true;
            }).ToList();
        }
        if (iot)
            result.Sections["quality"] = await Run("SELECT quality,count(*) AS samples FROM {{iot}} WHERE " + where + " GROUP BY quality ORDER BY samples DESC LIMIT $cap",
                new Dictionary<string, object?>(parameters) { ["cap"] = query.Limit + 1 }, ["quality", "samples"], from, to, query.Limit, token, "Reported quality code distribution over all signal samples, including string/null numeric values.");
        return result;
    }
}
