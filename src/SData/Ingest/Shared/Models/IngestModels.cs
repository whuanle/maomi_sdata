using System.Text.Json;

namespace SData.Ingest;

/// <summary>
/// 日志接入项（简单 JSON 格式）.
/// </summary>
public class IngestLogItem
{
    /// <summary>
    /// 时间戳（毫秒，缺省 = 当前时间）.
    /// </summary>
    public long? TimestampMs { get; set; }

    /// <summary>
    /// 服务名.
    /// </summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>
    /// 级别.
    /// </summary>
    public string? Severity { get; set; }

    /// <summary>
    /// 正文.
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// trace id.
    /// </summary>
    public string? TraceId { get; set; }

    /// <summary>
    /// span id.
    /// </summary>
    public string? SpanId { get; set; }

    /// <summary>
    /// 属性（JSON 对象，绑定后保留原始元素直通，避免字典二跳转换）.
    /// </summary>
    public JsonElement? Attributes { get; set; }

    /// <summary>
    /// 资源（JSON 对象，绑定后保留原始元素直通）.
    /// </summary>
    public JsonElement? Resource { get; set; }
}

/// <summary>
/// 链路 span 接入项.
/// </summary>
public class IngestSpanItem
{
    /// <summary>trace id.</summary>
    public string TraceId { get; set; } = string.Empty;

    /// <summary>span id.</summary>
    public string SpanId { get; set; } = string.Empty;

    /// <summary>父 span id.</summary>
    public string? ParentSpanId { get; set; }

    /// <summary>开始时间（毫秒）.</summary>
    public long? TimestampMs { get; set; }

    /// <summary>耗时（微秒）.</summary>
    public long DurationUs { get; set; }

    /// <summary>服务名.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>span 名称.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>类型（internal/client/server/producer/consumer）.</summary>
    public string? Kind { get; set; }

    /// <summary>状态（unset/ok/error）.</summary>
    public string? Status { get; set; }

    /// <summary>属性（JSON 对象，绑定后保留原始元素直通）.</summary>
    public JsonElement? Attributes { get; set; }

    /// <summary>资源（JSON 对象，绑定后保留原始元素直通）.</summary>
    public JsonElement? Resource { get; set; }
}

/// <summary>
/// 指标接入项.
/// </summary>
public class IngestMetricItem
{
    /// <summary>指标名.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>时间戳（毫秒）.</summary>
    public long? TimestampMs { get; set; }

    /// <summary>值.</summary>
    public double Value { get; set; }

    /// <summary>标签（JSON 对象，绑定后保留原始元素直通）.</summary>
    public JsonElement? Labels { get; set; }

    /// <summary>类型（gauge/sum）.</summary>
    public string? Kind { get; set; }
}

/// <summary>
/// 遥测接入项.
/// </summary>
public class IngestTelemetryItem
{
    /// <summary>设备标识.</summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>信号名.</summary>
    public string Signal { get; set; } = string.Empty;

    /// <summary>时间戳（毫秒）.</summary>
    public long? TimestampMs { get; set; }

    /// <summary>数值.</summary>
    public double? NumValue { get; set; }

    /// <summary>字符串值.</summary>
    public string? StrValue { get; set; }

    /// <summary>质量码：0 = good.</summary>
    public int Quality { get; set; }
}

/// <summary>
/// 接入响应.
/// </summary>
public class IngestResponse
{
    /// <summary>
    /// 接受条数.
    /// </summary>
    public long Accepted { get; set; }

    /// <summary>
    /// 丢弃条数（缓冲满）.
    /// </summary>
    public long Dropped { get; set; }
}
