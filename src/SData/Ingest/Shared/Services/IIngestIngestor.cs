using SData.Store.Models;

namespace SData.Ingest;

/// <summary>
/// 接入服务接口（实现在 Core，Api 通过接口调用，保持分层单向）.
/// </summary>
public interface IIngestIngestor
{
    /// <summary>
    /// 单条日志入缓冲.
    /// </summary>
    /// <param name="record">日志记录.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    bool EnqueueLog(LogRecord record);

    /// <summary>
    /// 单条 span 入缓冲.
    /// </summary>
    /// <param name="record">span 记录.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    bool EnqueueSpan(SpanRecord record);

    /// <summary>
    /// 单条指标入缓冲.
    /// </summary>
    /// <param name="record">指标点.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    bool EnqueueMetric(MetricPointRecord record);

    /// <summary>
    /// 批量日志入缓冲（OTLP 解析路径使用，整批复用字节预算检查）.
    /// </summary>
    /// <param name="records">日志记录.</param>
    /// <returns>接受/丢弃条数.</returns>
    (long Accepted, long Dropped) EnqueueLogs(IReadOnlyList<LogRecord> records);

    /// <summary>
    /// 批量 span 入缓冲（OTLP 解析路径使用）.
    /// </summary>
    /// <param name="records">span 记录.</param>
    /// <returns>接受/丢弃条数.</returns>
    (long Accepted, long Dropped) EnqueueSpans(IReadOnlyList<SpanRecord> records);

    /// <summary>
    /// 批量指标点入缓冲（OTLP 解析路径使用）.
    /// </summary>
    /// <param name="records">指标点.</param>
    /// <returns>接受/丢弃条数.</returns>
    (long Accepted, long Dropped) EnqueueMetrics(IReadOnlyList<MetricPointRecord> records);

    /// <summary>
    /// 单条遥测入缓冲.
    /// </summary>
    /// <param name="record">遥测记录.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    bool EnqueueTelemetry(TelemetryRecord record);

    /// <summary>预检本批原始字节是否仍在接入缓冲预算内.</summary>
    bool CanAcceptBytes(long bytes) => true;

    /// <summary>
    /// 批量接入日志.
    /// </summary>
    /// <param name="items">日志项.</param>
    /// <returns>接受/丢弃条数.</returns>
    IngestResponse IngestLogs(IEnumerable<IngestLogItem> items);

    /// <summary>
    /// 批量接入 span.
    /// </summary>
    /// <param name="items">span 项.</param>
    /// <returns>接受/丢弃条数.</returns>
    IngestResponse IngestSpans(IEnumerable<IngestSpanItem> items);

    /// <summary>
    /// 批量接入指标.
    /// </summary>
    /// <param name="items">指标项.</param>
    /// <returns>接受/丢弃条数.</returns>
    IngestResponse IngestMetrics(IEnumerable<IngestMetricItem> items);

    /// <summary>
    /// 批量接入遥测.
    /// </summary>
    /// <param name="items">遥测项.</param>
    /// <returns>接受/丢弃条数.</returns>
    IngestResponse IngestTelemetry(IEnumerable<IngestTelemetryItem> items);
}

/// <summary>
/// 接入 API Key 校验接口（实现在 Core）.
/// </summary>
public interface IIngestKeyValidator
{
    /// <summary>
    /// 校验 key.
    /// </summary>
    /// <param name="apiKey">明文 key.</param>
    /// <returns>是否有效.</returns>
    Task<bool> ValidateAsync(string apiKey);
}

/// <summary>
/// OTLP/HTTP(JSON) 解析接口（实现在 Core）.
/// </summary>
public interface IOtlpParser
{
    /// <summary>
    /// 解析 OTLP logs JSON.
    /// </summary>
    /// <param name="json">请求体.</param>
    /// <returns>日志记录.</returns>
    List<SData.Store.Models.LogRecord> ParseLogs(string json);

    /// <summary>
    /// 解析 OTLP traces JSON.
    /// </summary>
    /// <param name="json">请求体.</param>
    /// <returns>span 记录.</returns>
    List<SData.Store.Models.SpanRecord> ParseTraces(string json);

    /// <summary>
    /// 解析 OTLP metrics JSON.
    /// </summary>
    /// <param name="json">请求体.</param>
    /// <returns>指标点.</returns>
    List<SData.Store.Models.MetricPointRecord> ParseMetrics(string json);
}

/// <summary>
/// OTLP/HTTP(proto) 解析接口（实现在 Core；接口签名收发字节，避免 Shared 依赖 proto 生成类型）.
/// </summary>
public interface IOtlpProtoParser
{
    /// <summary>
    /// 解析 OTLP logs proto 报文（已解压的 ExportLogsServiceRequest 字节）.
    /// </summary>
    /// <param name="body">proto 字节.</param>
    /// <returns>日志记录.</returns>
    List<SData.Store.Models.LogRecord> ParseLogs(byte[] body);

    /// <summary>
    /// 解析 OTLP logs proto 报文（缓冲区切片，避免整包拷贝）.
    /// </summary>
    /// <param name="buffer">承载报文的缓冲区.</param>
    /// <param name="offset">起始偏移.</param>
    /// <param name="count">字节数.</param>
    /// <returns>日志记录.</returns>
    List<SData.Store.Models.LogRecord> ParseLogs(byte[] buffer, int offset, int count);

    /// <summary>
    /// 解析 OTLP traces proto 报文（已解压的 ExportTraceServiceRequest 字节）.
    /// </summary>
    /// <param name="body">proto 字节.</param>
    /// <returns>span 记录.</returns>
    List<SData.Store.Models.SpanRecord> ParseTraces(byte[] body);

    /// <summary>
    /// 解析 OTLP traces proto 报文（缓冲区切片，避免整包拷贝）.
    /// </summary>
    /// <param name="buffer">承载报文的缓冲区.</param>
    /// <param name="offset">起始偏移.</param>
    /// <param name="count">字节数.</param>
    /// <returns>span 记录.</returns>
    List<SData.Store.Models.SpanRecord> ParseTraces(byte[] buffer, int offset, int count);

    /// <summary>
    /// 解析 OTLP metrics proto 报文（已解压的 ExportMetricsServiceRequest 字节）.
    /// </summary>
    /// <param name="body">proto 字节.</param>
    /// <returns>指标点.</returns>
    List<SData.Store.Models.MetricPointRecord> ParseMetrics(byte[] body);

    /// <summary>
    /// 解析 OTLP metrics proto 报文（缓冲区切片，避免整包拷贝）.
    /// </summary>
    /// <param name="buffer">承载报文的缓冲区.</param>
    /// <param name="offset">起始偏移.</param>
    /// <param name="count">字节数.</param>
    /// <returns>指标点.</returns>
    List<SData.Store.Models.MetricPointRecord> ParseMetrics(byte[] buffer, int offset, int count);
}
