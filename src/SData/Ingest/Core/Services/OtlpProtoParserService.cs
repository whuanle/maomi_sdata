using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using SData.Store.Models;

namespace SData.Ingest.Services;

/// <summary>
/// IOtlpProtoParser 实现（包装静态解析器 OtlpProtoParser）.
/// </summary>
public class OtlpProtoParserService : SData.Ingest.IOtlpProtoParser
{
    /// <inheritdoc/>
    public List<LogRecord> ParseLogs(byte[] body) => OtlpProtoParser.ParseLogs(ExportLogsServiceRequest.Parser.ParseFrom(body));

    /// <inheritdoc/>
    public List<SpanRecord> ParseTraces(byte[] body) => OtlpProtoParser.ParseTraces(ExportTraceServiceRequest.Parser.ParseFrom(body));

    /// <inheritdoc/>
    public List<MetricPointRecord> ParseMetrics(byte[] body) => OtlpProtoParser.ParseMetrics(ExportMetricsServiceRequest.Parser.ParseFrom(body));
}
