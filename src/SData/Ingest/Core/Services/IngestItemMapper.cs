using SData.Store.Models;

namespace SData.Ingest.Services;

/// <summary>
/// Ingest*Item → Store 记录映射（HTTP /ingest 与 MQ json 格式共用）.
/// </summary>
public static class IngestItemMapper
{
    /// <summary>
    /// 日志项 → 日志记录（缺省值填充：service=unknown、severity=INFO、timestamp=当前时间）.
    /// </summary>
    /// <param name="item">日志项.</param>
    /// <returns>日志记录.</returns>
    public static LogRecord ToLogRecord(IngestLogItem item)
    {
        return new LogRecord
        {
            Timestamp = FromMs(item.TimestampMs),
            Service = string.IsNullOrEmpty(item.Service) ? "unknown" : item.Service,
            Severity = string.IsNullOrEmpty(item.Severity) ? "INFO" : item.Severity.ToUpperInvariant(),
            Body = item.Body ?? string.Empty,
            TraceId = item.TraceId,
            SpanId = item.SpanId,
            AttributesJson = IngestJsonSerializer.Serialize(item.Attributes),
            ResourceJson = IngestJsonSerializer.Serialize(item.Resource),
        };
    }

    private static DateTimeOffset FromMs(long? timestampMs)
    {
        return timestampMs.HasValue && timestampMs.Value > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestampMs.Value)
            : DateTimeOffset.UtcNow;
    }

}
