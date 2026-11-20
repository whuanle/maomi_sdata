using SData.Ingest.Services;

namespace SData.Ingest;

/// <summary>
/// IOtlpParser 实现（包装静态解析器）.
/// </summary>
public class OtlpParser : IOtlpParser
{
    /// <inheritdoc/>
    public List<SData.Store.Models.LogRecord> ParseLogs(string json) => OtlpLogParser.Parse(json);

    /// <inheritdoc/>
    public List<SData.Store.Models.SpanRecord> ParseTraces(string json) => OtlpTraceParser.Parse(json);

    /// <inheritdoc/>
    public List<SData.Store.Models.MetricPointRecord> ParseMetrics(string json) => OtlpMetricParser.Parse(json);
}
