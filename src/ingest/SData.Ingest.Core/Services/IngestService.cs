using System.Text.Json;
using SData.Store;
using SData.Store.Models;

namespace SData.Ingest.Services;

/// <summary>
/// 接入服务：简单 JSON 格式 → Store 记录 → 内存缓冲.
/// </summary>
public class IngestService : IIngestIngestor
{
    private readonly IIngestBuffer _buffer;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestService"/> class.
    /// </summary>
    /// <param name="buffer">缓冲.</param>
    public IngestService(IIngestBuffer buffer)
    {
        _buffer = buffer;
    }

    /// <summary>
    /// 单条日志入缓冲（OTLP 解析路径使用）.
    /// </summary>
    /// <param name="record">日志记录.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    public bool EnqueueLog(LogRecord record) => _buffer.TryEnqueueLog(record);

    /// <summary>
    /// 单条 span 入缓冲（OTLP 解析路径使用）.
    /// </summary>
    /// <param name="record">span 记录.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    public bool EnqueueSpan(SpanRecord record) => _buffer.TryEnqueueSpan(record);

    /// <summary>
    /// 单条指标入缓冲（OTLP 解析路径使用）.
    /// </summary>
    /// <param name="record">指标点.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    public bool EnqueueMetric(MetricPointRecord record) => _buffer.TryEnqueueMetric(record);

    /// <summary>
    /// 单条遥测入缓冲（OTLP 解析路径使用）.
    /// </summary>
    /// <param name="record">遥测记录.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    public bool EnqueueTelemetry(TelemetryRecord record) => _buffer.TryEnqueueTelemetry(record);

    /// <summary>
    /// 批量接入日志.
    /// </summary>
    /// <param name="items">日志项.</param>
    /// <returns>接受/丢弃条数.</returns>
    public IngestResponse IngestLogs(IEnumerable<IngestLogItem> items)
    {
        long accepted = 0;
        long dropped = 0;
        foreach (var item in items)
        {
            var record = IngestItemMapper.ToLogRecord(item);
            if (_buffer.TryEnqueueLog(record))
            {
                accepted++;
            }
            else
            {
                dropped++;
            }
        }

        return new IngestResponse { Accepted = accepted, Dropped = dropped };
    }

    /// <summary>
    /// 批量接入 span.
    /// </summary>
    /// <param name="items">span 项.</param>
    /// <returns>接受/丢弃条数.</returns>
    public IngestResponse IngestSpans(IEnumerable<IngestSpanItem> items)
    {
        long accepted = 0;
        long dropped = 0;
        foreach (var item in items)
        {
            var record = new SpanRecord
            {
                TraceId = item.TraceId ?? string.Empty,
                SpanId = item.SpanId ?? string.Empty,
                ParentSpanId = item.ParentSpanId,
                Timestamp = FromMs(item.TimestampMs),
                DurationUs = item.DurationUs,
                Service = string.IsNullOrEmpty(item.Service) ? "unknown" : item.Service,
                Name = item.Name ?? string.Empty,
                Kind = string.IsNullOrEmpty(item.Kind) ? "internal" : item.Kind,
                Status = string.IsNullOrEmpty(item.Status) ? "unset" : item.Status,
                AttributesJson = ToJson(item.Attributes),
                ResourceJson = null,
            };

            if (_buffer.TryEnqueueSpan(record))
            {
                accepted++;
            }
            else
            {
                dropped++;
            }
        }

        return new IngestResponse { Accepted = accepted, Dropped = dropped };
    }

    /// <summary>
    /// 批量接入指标.
    /// </summary>
    /// <param name="items">指标项.</param>
    /// <returns>接受/丢弃条数.</returns>
    public IngestResponse IngestMetrics(IEnumerable<IngestMetricItem> items)
    {
        long accepted = 0;
        long dropped = 0;
        foreach (var item in items)
        {
            var record = new MetricPointRecord
            {
                Name = item.Name ?? string.Empty,
                Timestamp = FromMs(item.TimestampMs),
                Value = item.Value,
                LabelsJson = ToJson(item.Labels) ?? "{}",
                Kind = string.IsNullOrEmpty(item.Kind) ? "gauge" : item.Kind,
            };

            if (_buffer.TryEnqueueMetric(record))
            {
                accepted++;
            }
            else
            {
                dropped++;
            }
        }

        return new IngestResponse { Accepted = accepted, Dropped = dropped };
    }

    /// <summary>
    /// 批量接入遥测.
    /// </summary>
    /// <param name="items">遥测项.</param>
    /// <returns>接受/丢弃条数.</returns>
    public IngestResponse IngestTelemetry(IEnumerable<IngestTelemetryItem> items)
    {
        long accepted = 0;
        long dropped = 0;
        foreach (var item in items)
        {
            var record = new TelemetryRecord
            {
                DeviceKey = item.DeviceKey ?? string.Empty,
                Signal = item.Signal ?? string.Empty,
                Timestamp = FromMs(item.TimestampMs),
                NumValue = item.NumValue,
                StrValue = item.StrValue,
                Quality = item.Quality,
            };

            if (_buffer.TryEnqueueTelemetry(record))
            {
                accepted++;
            }
            else
            {
                dropped++;
            }
        }

        return new IngestResponse { Accepted = accepted, Dropped = dropped };
    }

    private static DateTimeOffset FromMs(long? timestampMs)
    {
        return timestampMs.HasValue && timestampMs.Value > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestampMs.Value)
            : DateTimeOffset.UtcNow;
    }

    private static string? ToJson(Dictionary<string, object?>? values)
    {
        if (values == null || values.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(values);
    }
}
