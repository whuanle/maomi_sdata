namespace SData.Store.Models;

/// <summary>
/// 日志记录.
/// </summary>
public class LogRecord
{
    /// <summary>
    /// 行级雪花 id（0 = 未赋值，落库时由写入器统一补齐；幂等去重/行级定位用）.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 时间戳.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// 服务名.
    /// </summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>
    /// 严重级别（TRACE/DEBUG/INFO/WARN/ERROR/FATAL 或 OTLP severity_text）.
    /// </summary>
    public string Severity { get; set; } = "INFO";

    /// <summary>
    /// 日志正文.
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// 关联 trace id.
    /// </summary>
    public string? TraceId { get; set; }

    /// <summary>
    /// 关联 span id.
    /// </summary>
    public string? SpanId { get; set; }

    /// <summary>
    /// 属性（JSON 对象字符串）.
    /// </summary>
    public string? AttributesJson { get; set; }

    /// <summary>
    /// 资源信息（JSON 对象字符串）.
    /// </summary>
    public string? ResourceJson { get; set; }

    /// <summary>
    /// 日志来源（ES Bulk 的 _index / Filebeat 索引名；其余通道为空）.
    /// </summary>
    public string? Source { get; set; }
}

/// <summary>
/// Span 记录.
/// </summary>
public class SpanRecord
{
    /// <summary>
    /// trace id.
    /// </summary>
    public string TraceId { get; set; } = string.Empty;

    /// <summary>
    /// span id.
    /// </summary>
    public string SpanId { get; set; } = string.Empty;

    /// <summary>
    /// 父 span id.
    /// </summary>
    public string? ParentSpanId { get; set; }

    /// <summary>
    /// 开始时间.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// 耗时（微秒）.
    /// </summary>
    public long DurationUs { get; set; }

    /// <summary>
    /// 服务名.
    /// </summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>
    /// span 名称.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 类型：internal/client/server/producer/consumer.
    /// </summary>
    public string Kind { get; set; } = "internal";

    /// <summary>
    /// 状态：unset/ok/error.
    /// </summary>
    public string Status { get; set; } = "unset";

    /// <summary>
    /// 属性（JSON 对象字符串）.
    /// </summary>
    public string? AttributesJson { get; set; }

    /// <summary>
    /// 事件（JSON 数组字符串）.
    /// </summary>
    public string? EventsJson { get; set; }

    /// <summary>
    /// 资源信息（JSON 对象字符串）.
    /// </summary>
    public string? ResourceJson { get; set; }
}

/// <summary>
/// 指标数据点.
/// </summary>
public class MetricPointRecord
{
    /// <summary>
    /// 指标名.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 时间戳.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// 值.
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// 标签（JSON 对象字符串）.
    /// </summary>
    public string? LabelsJson { get; set; }

    /// <summary>
    /// 类型：gauge / sum / histogram.
    /// </summary>
    public string Kind { get; set; } = "gauge";
}

/// <summary>
/// 物联网遥测记录（窄表）.
/// </summary>
public class TelemetryRecord
{
    /// <summary>
    /// 设备标识.
    /// </summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>
    /// 信号名（温度/压力/电流等）.
    /// </summary>
    public string Signal { get; set; } = string.Empty;

    /// <summary>
    /// 时间戳.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// 数值.
    /// </summary>
    public double? NumValue { get; set; }

    /// <summary>
    /// 字符串值（状态等）.
    /// </summary>
    public string? StrValue { get; set; }

    /// <summary>
    /// 质量码：0 = good.
    /// </summary>
    public int Quality { get; set; }
}

/// <summary>
/// RUM 行为事件记录（pageview/vital/error/click/custom，T610）.
/// </summary>
public class RumEventRecord
{
    /// <summary>项目标识.</summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>会话标识.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>事件类型（pageview/vital/error/click/custom）.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>事件名（vital 的 LCP/INP/CLS、custom 自定义名、error message）.</summary>
    public string? EventName { get; set; }

    /// <summary>页面路径.</summary>
    public string? PagePath { get; set; }

    /// <summary>数值（vital 指标值、click 坐标标量等）.</summary>
    public double? Value { get; set; }

    /// <summary>时间戳.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>事件载荷（JSON 对象字符串，其余字段原样保留）.</summary>
    public string? PayloadJson { get; set; }
}

/// <summary>
/// RUM 会话记录（pageview 触发的会话快照，T610）.
/// </summary>
public class RumSessionRecord
{
    /// <summary>项目标识.</summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>会话标识.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>时间戳（会话活跃点）.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>用户代理.</summary>
    public string? UserAgent { get; set; }

    /// <summary>站点域名.</summary>
    public string? Domain { get; set; }
}

/// <summary>
/// RUM 会话回放分片（replay-chunk，gzip blob base64，T610）.
/// </summary>
public class RumReplayRecord
{
    /// <summary>项目标识.</summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>会话标识.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>分片序号.</summary>
    public long Seq { get; set; }

    /// <summary>分片内容（gzip blob base64）.</summary>
    public string BlobBase64 { get; set; } = string.Empty;

    /// <summary>时间戳.</summary>
    public DateTimeOffset Timestamp { get; set; }
}
