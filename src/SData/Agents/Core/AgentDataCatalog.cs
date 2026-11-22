using SData.Infra.Exceptions;

namespace SData.Agents;

internal sealed record AgentField(string Name, string Type, bool Nullable, string Meaning, bool Readable = true, string? Unit = null)
{
    public bool Numeric => Type is "DOUBLE" or "BIGINT" or "INTEGER";
}

internal sealed record AgentSource(string Domain, string Prefix, string Meaning, string RowMeaning, AgentField[] Fields);

internal static class AgentDataCatalog
{
    public static readonly string[] LabelKeys = ["service.name", "service_name", "host.name", "environment", "deployment.environment", "job", "instance", "unit"];
    public static readonly AgentSource[] Sources =
    [
        new("logs", "logs_", "Application and infrastructure logs", "One log record", [
            new("id", "BIGINT", true, "Record id"), new("ts", "TIMESTAMP", false, "UTC event time"),
            new("service", "VARCHAR", false, "Service name"), new("severity", "VARCHAR", false, "Log severity"),
            new("body", "VARCHAR", false, "Untrusted log text"), new("trace_id", "VARCHAR", true, "Trace correlation id"),
            new("span_id", "VARCHAR", true, "Span correlation id"), new("source", "VARCHAR", true, "Ingestion source"),
            new("attributes", "VARCHAR", true, "JSON attributes; raw content excluded", false), new("resource", "VARCHAR", true, "JSON resource; raw content excluded", false)]),
        new("traces", "traces_", "Distributed tracing spans", "One span, not one trace", [
            new("ts", "TIMESTAMP", false, "UTC span start"), new("trace_id", "VARCHAR", false, "Trace id"),
            new("span_id", "VARCHAR", false, "Span id"), new("parent_span_id", "VARCHAR", true, "Parent span id"),
            new("duration_us", "BIGINT", false, "Span duration", Unit: "microseconds"), new("service", "VARCHAR", false, "Service name"),
            new("name", "VARCHAR", false, "Operation name"), new("kind", "VARCHAR", false, "Span kind"), new("status", "VARCHAR", false, "Span status"),
            new("attributes", "VARCHAR", true, "Raw attributes excluded", false), new("events", "VARCHAR", true, "Raw events excluded", false),
            new("resource", "VARCHAR", true, "Raw resource excluded", false)]),
        new("metrics", "metrics_", "Metric samples", "One metric sample with a label set; sum/avg are not counter rates", [
            new("ts", "TIMESTAMP", false, "UTC sample time"), new("name", "VARCHAR", false, "Metric name"),
            new("value", "DOUBLE", false, "Sample value; unit depends on instrument"), new("kind", "VARCHAR", false, "Instrument kind"),
            new("labels", "VARCHAR", true, "Raw labels excluded; list_metric_labels exposes safe dimensions", false)]),
        new("iot", "iot_", "HTTP/MQTT device telemetry", "One device signal sample; MQTT shares this table family", [
            new("ts", "TIMESTAMP", false, "UTC sample time"), new("device_key", "VARCHAR", false, "Device identifier, not credential"),
            new("signal", "VARCHAR", false, "Signal name"), new("num_value", "DOUBLE", true, "Numeric sample"),
            new("str_value", "VARCHAR", true, "Untrusted string sample"), new("quality", "INTEGER", false, "Reported quality code; not inferred status")]),
        new("sessions", "rum_session_", "RUM page-view reports", "One page-view report, not one unique session or user", [
            new("ts", "TIMESTAMP", false, "UTC report time"), new("project_key", "VARCHAR", false, "Project identifier"),
            new("session_id", "VARCHAR", false, "Session identifier; distinct sessions are not unique users"),
            new("domain", "VARCHAR", true, "Reported site domain"), new("user_agent", "VARCHAR", true, "Raw user agent excluded", false)]),
        new("rum_events", "rum_event_", "RUM vitals/errors/click events", "One RUM event", [
            new("ts", "TIMESTAMP", false, "UTC event time"), new("project_key", "VARCHAR", false, "Project identifier"),
            new("session_id", "VARCHAR", false, "Session id"), new("event_type", "VARCHAR", false, "Event category"),
            new("event_name", "VARCHAR", true, "Event name"), new("page_path", "VARCHAR", true, "Reported page path"),
            new("value", "DOUBLE", true, "Vital value; LCP/INP/FCP/TTFB in ms, CLS unitless"),
            new("payload", "VARCHAR", true, "Raw payload excluded", false)]),
    ];

    public static AgentSource Source(string domain) => Sources.FirstOrDefault(x => x.Domain == domain)
        ?? throw BusinessException.Throw(400, "未知数据源，请先调用 list_data_sources");

    public static AgentField Field(AgentSource source, string name) => source.Fields.FirstOrDefault(x => x.Name == name && x.Readable)
        ?? throw BusinessException.Throw(400, "字段不在允许查询清单中");

    public static object Describe(AgentSource source) => new
    {
        source.Domain, source.Prefix, source.Meaning, source.RowMeaning,
        fields = source.Fields.Select(f => new { f.Name, f.Type, f.Nullable, f.Meaning, f.Readable, f.Unit,
            operations = !f.Readable ? Array.Empty<string>() : f.Numeric ? ["filter", "group", "count", "distinct_count", "sum", "avg", "min", "max", "quantile"] : new[] { "filter", "group", "count", "distinct_count", "min", "max" } }),
        tools = new[] { "query_data", "aggregate_data", "compare_periods", "get_field_values" },
        projectRequired = source.Domain is "sessions" or "rum_events",
    };
}
