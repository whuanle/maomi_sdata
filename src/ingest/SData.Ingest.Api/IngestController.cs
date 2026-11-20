using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SData.Infra.Configuration;

namespace SData.Ingest.Api;

/// <summary>
/// 数据接入接口：OTLP/HTTP(JSON) + 简单 JSON 批量格式.
/// </summary>
[ApiController]
[Route("ingest")]
[ServiceFilter(typeof(IngestApiKeyFilter))]
public class IngestController : ControllerBase
{
    private readonly SData.Ingest.IIngestIngestor _ingestor;
    private readonly SData.Ingest.IOtlpParser _parser;
    private readonly SData.Ingest.IOtlpProtoParser _protoParser;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestController"/> class.
    /// </summary>
    /// <param name="ingestor">接入服务.</param>
    /// <param name="parser">OTLP 解析器.</param>
    /// <param name="protoParser">OTLP proto 解析器.</param>
    public IngestController(SData.Ingest.IIngestIngestor ingestor, SData.Ingest.IOtlpParser parser, SData.Ingest.IOtlpProtoParser protoParser)
    {
        _ingestor = ingestor;
        _parser = parser;
        _protoParser = protoParser;
    }

    /// <summary>
    /// OTLP logs 接入（旧路径，转发至标准 /v1/logs 处理链；保留一个版本周期后废弃）.
    /// </summary>
    /// <returns>OTLP 响应.</returns>
    [HttpPost("otlp/v1/logs")]
    public Task<IActionResult> OtlpLogs()
    {
        return OtlpV1Controller.ExportAsync(Request, _protoParser.ParseLogs, _parser.ParseLogs, _ingestor.EnqueueLog);
    }

    /// <summary>
    /// OTLP traces 接入（旧路径，转发至标准 /v1/traces 处理链；保留一个版本周期后废弃）.
    /// </summary>
    /// <returns>OTLP 响应.</returns>
    [HttpPost("otlp/v1/traces")]
    public Task<IActionResult> OtlpTraces()
    {
        return OtlpV1Controller.ExportAsync(Request, _protoParser.ParseTraces, _parser.ParseTraces, _ingestor.EnqueueSpan);
    }

    /// <summary>
    /// OTLP metrics 接入（旧路径，转发至标准 /v1/metrics 处理链；保留一个版本周期后废弃）.
    /// </summary>
    /// <returns>OTLP 响应.</returns>
    [HttpPost("otlp/v1/metrics")]
    public Task<IActionResult> OtlpMetrics()
    {
        return OtlpV1Controller.ExportAsync(Request, _protoParser.ParseMetrics, _parser.ParseMetrics, _ingestor.EnqueueMetric);
    }

    /// <summary>
    /// 简单 JSON 日志批量接入.
    /// </summary>
    /// <param name="items">日志项数组.</param>
    /// <returns>接受/丢弃条数.</returns>
    [HttpPost("logs")]
    public SData.Ingest.IngestResponse Logs([FromBody] SData.Ingest.IngestLogItem[] items)
    {
        return _ingestor.IngestLogs(items);
    }

    /// <summary>
    /// 简单 JSON 链路批量接入.
    /// </summary>
    /// <param name="items">span 项数组.</param>
    /// <returns>接受/丢弃条数.</returns>
    [HttpPost("traces")]
    public SData.Ingest.IngestResponse Traces([FromBody] SData.Ingest.IngestSpanItem[] items)
    {
        return _ingestor.IngestSpans(items);
    }

    /// <summary>
    /// 简单 JSON 指标批量接入.
    /// </summary>
    /// <param name="items">指标项数组.</param>
    /// <returns>接受/丢弃条数.</returns>
    [HttpPost("metrics")]
    public SData.Ingest.IngestResponse Metrics([FromBody] SData.Ingest.IngestMetricItem[] items)
    {
        return _ingestor.IngestMetrics(items);
    }

    /// <summary>
    /// 简单 JSON 遥测批量接入.
    /// </summary>
    /// <param name="items">遥测项数组.</param>
    /// <returns>接受/丢弃条数.</returns>
    [HttpPost("iot")]
    public SData.Ingest.IngestResponse Iot([FromBody] SData.Ingest.IngestTelemetryItem[] items)
    {
        return _ingestor.IngestTelemetry(items);
    }
}
