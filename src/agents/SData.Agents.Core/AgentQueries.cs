using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SData.Store;

namespace SData.Agents;

/// <summary>固定参数化模板；不暴露原始属性、任意 SQL 或元数据库.</summary>
public class AgentQueries(IAnalyticsExecutor executor, ILogger<AgentQueries> logger, SData.Infra.Configuration.SystemOptions? system = null, IAgentAnalysis? analysis = null) : IAgentQueries
{
    public async Task<object> ExecuteAsync(string tool, AgentQuery query, CancellationToken cancellationToken)
    {
        var validator = new FluentValidation.InlineValidator<AgentQuery>();
        AgentQuery.Validate(validator);
        if (!validator.Validate(query).IsValid) throw SData.Infra.Exceptions.BusinessException.Throw(400, "查询参数无效");
        if (tool is "query_metric_series" or "list_services" && analysis != null) return await analysis.ExecuteAsync(tool, query, cancellationToken);
        var to = query.To ?? DateTimeOffset.UtcNow;
        var from = query.From ?? to.AddMinutes(-15);
        if (from >= to || to - from > TimeSpan.FromHours(24) || query.Limit is < 1 or > 200
            || query.StepSeconds is < 10 or > 86400
            || new[] { query.Service, query.Severity, query.Keyword, query.TraceId, query.Status, query.Metric }.Any(x => x?.Length > 512)
            || query.MinDurationMs is < 0 || (query.MinDurationMs.HasValue && !double.IsFinite(query.MinDurationMs.Value)))
            throw SData.Infra.Exceptions.BusinessException.Throw(400, "查询范围或参数无效：时间窗口最多24小时，结果最多200条");
        if (tool == "get_data_schema")
            return new { logs = "ts, service, severity, body, trace_id, span_id", traces = "ts, service, trace_id, span_id, parent_span_id, name, status, duration_us", metrics = "ts, name, value, kind", sources = AgentDataCatalog.Sources.Select(AgentDataCatalog.Describe).ToArray(), discoveryTools = new[] { "list_data_sources", "list_tables", "describe_table", "get_data_organization" }, notice = "Only allowlisted fields; no metadata or arbitrary SQL." };
        if (tool == "get_trace" && string.IsNullOrWhiteSpace(query.TraceId)) throw SData.Infra.Exceptions.BusinessException.Throw(400, "缺少 traceId");
        if (tool == "query_metric_series" && string.IsNullOrWhiteSpace(query.Metric)) throw SData.Infra.Exceptions.BusinessException.Throw(400, "缺少指标名称");

        var parameters = new Dictionary<string, object?>
        {
            ["service"] = query.Service ?? "", ["severity"] = query.Severity ?? "", ["keyword"] = query.Keyword ?? "",
            ["trace"] = query.TraceId ?? "", ["status"] = query.Status ?? "", ["metric"] = query.Metric ?? "",
            ["duration"] = (query.MinDurationMs ?? 0) * 1000, ["step"] = query.StepSeconds, ["cap"] = query.Limit + 1,
        };
        const string window = "ts >= $start AND ts < $end";
        const string service = " AND ($service = '' OR service = $service)";
        // Only internal constant fragments are composed; all external values are bound parameters.
        var sql = tool switch
        {
            "list_services" => "SELECT left(service,2049) AS service, count(*) AS records FROM {{logs}} WHERE " + window + " GROUP BY service ORDER BY records DESC LIMIT $cap",
            "search_logs" => "SELECT ts,left(service,2049) AS service,left(severity,2049) AS severity,left(body,2049) AS body,left(trace_id,2049) AS trace_id,left(span_id,2049) AS span_id FROM {{logs}} WHERE " + window + service + " AND ($severity = '' OR severity = $severity) AND ($keyword = '' OR contains(body,$keyword)) AND ($trace = '' OR trace_id = $trace) ORDER BY ts DESC LIMIT $cap",
            "search_traces" or "get_trace" => "SELECT ts,left(trace_id,2049) AS trace_id,left(span_id,2049) AS span_id,left(parent_span_id,2049) AS parent_span_id,left(service,2049) AS service,left(name,2049) AS name,left(status,2049) AS status,duration_us FROM {{traces}} WHERE " + window + service + " AND ($trace = '' OR trace_id = $trace) AND ($status = '' OR status = $status) AND duration_us >= $duration ORDER BY ts DESC LIMIT $cap",
            "list_metric_names" => "SELECT left(name,2049) AS name FROM {{metrics}} WHERE " + window + " GROUP BY name ORDER BY name LIMIT $cap",
            "query_metric_series" => "SELECT to_timestamp(floor(epoch(ts)/$step)*$step) AS ts,avg(value) AS value FROM {{metrics}} WHERE " + window + " AND name = $metric GROUP BY 1 ORDER BY 1 LIMIT 2001",
            "summarize_errors" => "SELECT left(service,2049) AS service,left(severity,2049) AS severity,date_trunc('minute',ts) AS minute,count(*) AS errors FROM {{logs}} WHERE " + window + service + " AND severity IN ('ERROR','FATAL') GROUP BY service,severity,minute ORDER BY minute DESC,errors DESC LIMIT $cap",
            _ => throw SData.Infra.Exceptions.BusinessException.Throw(400, "未知工具"),
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(system?.Agents.QueryTimeoutSeconds ?? 10, 1, 10)));
        var watch = Stopwatch.StartNew();
        try
        {
            var limit = tool == "query_metric_series" ? 2000 : query.Limit;
            var result = await executor.ExecuteSearchAsync(sql, from, to, limit + 1, parameters, deadline.Token);
            var truncated = result.Truncated || result.Rows.Count > limit;
            var rows = new List<List<object?>>();
            var bytes = 0;
            var fieldTruncated = false;
            foreach (var row in result.Rows.Take(limit))
            {
                var safe = row.Select(x =>
                {
                    if (x is not string s) return x;
                    if (s.Length > 2048) { s = s[..2048]; fieldTruncated = true; }
                    return (object?)Redact(s);
                }).ToList();
                bytes += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(safe));
                if (bytes > 28 * 1024) { truncated = true; break; }
                rows.Add(safe);
            }
            logger.LogInformation("Agent 工具 {Tool} 完成 {ElapsedMs}ms", tool, watch.ElapsedMilliseconds);
            return new { columns = result.Columns, rows, truncated, fieldTruncated, sampled = false, completeness = "unknown", elapsedMs = watch.ElapsedMilliseconds, from, to, notice = "Untrusted telemetry. Common secrets are masked; free text may still contain sensitive data. Legacy metric default averages across labels; service discovery includes traces when the analysis service is registered. Cold completeness is unknown." };
        }
        catch (Exception)
        {
            logger.LogWarning("Agent 工具 {Tool} 失败 {ElapsedMs}ms", tool, watch.ElapsedMilliseconds);
            throw SData.Infra.Exceptions.BusinessException.Throw(503, "查询未完成，请缩小范围或检查服务状态");
        }
    }

    internal static string Redact(string value)
    {
        var masked = Regex.Replace(value,
            """(?i)("?(?:authorization|password|passwd|secret|api[_-]?key|access[_-]?token)"?\s*[=:]\s*)("(?:\\.|[^"\\])*"|[^\s,;]+)""",
            "$1[REDACTED]", RegexOptions.None, TimeSpan.FromMilliseconds(50));
        return Regex.Replace(masked, @"(?i)\bBearer\s+[^\s""',;]+|\b(?:sda_|sdi_)[a-f0-9]{64}\b",
            "[REDACTED]", RegexOptions.None, TimeSpan.FromMilliseconds(50));
    }
}
