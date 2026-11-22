using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using OtLogs = OpenTelemetry.Proto.Logs.V1;
using SData.Store.Models;

namespace SData.Ingest.Services;

/// <summary>
/// OTLP/HTTP(proto) 解析：Export*ServiceRequest → 领域记录，与 Otlp*Parser(JSON) 语义对齐.
/// </summary>
public static class OtlpProtoParser
{
    private static readonly string[] SpanKinds = { "unspecified", "internal", "server", "client", "producer", "consumer" };

    /// <summary>
    /// 解析 OTLP logs proto 请求.
    /// </summary>
    /// <param name="request">ExportLogsServiceRequest.</param>
    /// <returns>日志记录.</returns>
    public static List<LogRecord> ParseLogs(ExportLogsServiceRequest request)
    {
        var records = new List<LogRecord>();
        foreach (var resourceLogs in request.ResourceLogs)
        {
            var resourceJson = ResourceToJson(resourceLogs.Resource);
            var service = OtlpJson.ExtractServiceName(resourceJson);

            foreach (var scopeLogs in resourceLogs.ScopeLogs)
            {
                foreach (var logRecord in scopeLogs.LogRecords)
                {
                    var timestamp = NanoToTimestamp(logRecord.TimeUnixNano, logRecord.ObservedTimeUnixNano);
                    var severity = logRecord.SeverityText;
                    if (string.IsNullOrEmpty(severity))
                    {
                        severity = SeverityText(logRecord.SeverityNumber);
                    }

                    records.Add(new LogRecord
                    {
                        Timestamp = timestamp,
                        Service = service,
                        Severity = severity,
                        Body = AnyValueToString(logRecord.Body),
                        TraceId = BytesToHex(logRecord.TraceId),
                        SpanId = BytesToHex(logRecord.SpanId),
                        AttributesJson = AttributesToJson(logRecord.Attributes),
                        ResourceJson = resourceJson,
                    });
                }
            }
        }

        return records;
    }

    /// <summary>
    /// 解析 OTLP traces proto 请求.
    /// </summary>
    /// <param name="request">ExportTraceServiceRequest.</param>
    /// <returns>span 记录.</returns>
    public static List<SpanRecord> ParseTraces(ExportTraceServiceRequest request)
    {
        var records = new List<SpanRecord>();
        foreach (var resourceSpans in request.ResourceSpans)
        {
            var resourceJson = ResourceToJson(resourceSpans.Resource);
            var service = OtlpJson.ExtractServiceName(resourceJson);

            foreach (var scopeSpans in resourceSpans.ScopeSpans)
            {
                foreach (var span in scopeSpans.Spans)
                {
                    var startNs = (long)span.StartTimeUnixNano;
                    var endNs = (long)span.EndTimeUnixNano;
                    var startUs = startNs / 1000;
                    var endUs = endNs / 1000;
                    if (startNs <= 0)
                    {
                        startUs = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks / 10;
                        endUs = startUs;
                    }

                    records.Add(new SpanRecord
                    {
                        TraceId = BytesToHex(span.TraceId),
                        SpanId = BytesToHex(span.SpanId),
                        ParentSpanId = span.ParentSpanId.IsEmpty ? null : BytesToHex(span.ParentSpanId),
                        Timestamp = DateTimeOffset.UnixEpoch.AddTicks(startUs * 10),
                        DurationUs = Math.Max(0, endUs - startUs),
                        Service = service,
                        Name = span.Name,
                        Kind = SpanKinds[Math.Clamp((int)span.Kind, 0, SpanKinds.Length - 1)],
                        Status = span.Status?.Code switch { Status.Types.StatusCode.Ok => "ok", Status.Types.StatusCode.Error => "error", _ => "unset" },
                        AttributesJson = AttributesToJson(span.Attributes),
                        EventsJson = EventsToJson(span.Events),
                        ResourceJson = resourceJson,
                    });
                }
            }
        }

        return records;
    }

    /// <summary>
    /// 解析 OTLP metrics proto 请求：gauge/sum 数据点直接入桶，histogram 记录 count/sum 派生序列.
    /// </summary>
    /// <param name="request">ExportMetricsServiceRequest.</param>
    /// <returns>指标点.</returns>
    public static List<MetricPointRecord> ParseMetrics(ExportMetricsServiceRequest request)
    {
        var records = new List<MetricPointRecord>();
        foreach (var resourceMetrics in request.ResourceMetrics)
        {
            var resourceJson = ResourceToJson(resourceMetrics.Resource);
            var service = OtlpJson.ExtractServiceName(resourceJson);

            foreach (var scopeMetrics in resourceMetrics.ScopeMetrics)
            {
                foreach (var metric in scopeMetrics.Metrics)
                {
                    if (string.IsNullOrEmpty(metric.Name))
                    {
                        continue;
                    }

                    if (metric.Gauge != null)
                    {
                        ParsePoints(metric.Gauge.DataPoints, metric.Name, "gauge", resourceJson, service, records);
                    }

                    if (metric.Sum != null)
                    {
                        ParsePoints(metric.Sum.DataPoints, metric.Name, "sum", resourceJson, service, records);
                    }

                    if (metric.Histogram != null)
                    {
                        ParseHistogram(metric.Histogram.DataPoints, metric.Name, records);
                    }
                }
            }
        }

        return records;
    }

    private static void ParsePoints(
        RepeatedField<NumberDataPoint> points,
        string metricName,
        string kind,
        string? resourceJson,
        string service,
        List<MetricPointRecord> records)
    {
        foreach (var point in points)
        {
            if (!TryGetValue(point, out var value))
            {
                continue;
            }

            var timestamp = point.TimeUnixNano > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)point.TimeUnixNano / 1_000_000)
                : DateTimeOffset.UtcNow;
            var labelsJson = AttributesToJson(point.Attributes);
            if (labelsJson != null && resourceJson != null)
            {
                labelsJson = $"{{\"service\":{JsonSerializer.Serialize(service)},\"labels\":{labelsJson}}}";
            }
            else
            {
                labelsJson = resourceJson == null ? "{}" : $"{{\"service\":{JsonSerializer.Serialize(service)}}}";
            }

            records.Add(new MetricPointRecord
            {
                Name = metricName,
                Timestamp = timestamp,
                Value = value,
                LabelsJson = labelsJson,
                Kind = kind,
            });
        }
    }

    private static void ParseHistogram(RepeatedField<HistogramDataPoint> points, string metricName, List<MetricPointRecord> records)
    {
        foreach (var point in points)
        {
            var timestamp = point.TimeUnixNano > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)point.TimeUnixNano / 1_000_000)
                : DateTimeOffset.UtcNow;
            var labelsJson = AttributesToJson(point.Attributes) ?? "{}";
            if (point.Count > 0)
            {
                records.Add(new MetricPointRecord
                {
                    Name = metricName + ".count",
                    Timestamp = timestamp,
                    Value = point.Count,
                    LabelsJson = labelsJson,
                    Kind = "histogram",
                });
            }

            if (point.HasSum)
            {
                records.Add(new MetricPointRecord
                {
                    Name = metricName + ".sum",
                    Timestamp = timestamp,
                    Value = point.Sum,
                    LabelsJson = labelsJson,
                    Kind = "histogram",
                });
            }
        }
    }

    private static bool TryGetValue(NumberDataPoint point, out double value)
    {
        // proto3 oneof：asDouble 或 asInt 二选一
        if (point.ValueCase == NumberDataPoint.ValueOneofCase.AsDouble)
        {
            value = point.AsDouble;
            return true;
        }

        if (point.ValueCase == NumberDataPoint.ValueOneofCase.AsInt)
        {
            value = point.AsInt;
            return true;
        }

        value = 0;
        return false;
    }

    private static DateTimeOffset NanoToTimestamp(ulong timeUnixNano, ulong observedTimeUnixNano)
    {
        var nano = timeUnixNano > 0 ? timeUnixNano : observedTimeUnixNano;
        return nano > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)nano / 1_000_000)
            : DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// resource → JSON 对象字符串（属性转 JSON，形状与 OtlpJson.AttributesToJson 一致）；无属性返回 null.
    /// </summary>
    private static string? ResourceToJson(Resource? resource)
    {
        return resource == null ? null : AttributesToJson(resource.Attributes);
    }

    /// <summary>
    /// proto KeyValue 属性列表 → JSON 对象字符串（{"key": value}）；空返回 null.
    /// </summary>
    private static string? AttributesToJson(RepeatedField<KeyValue> attributes)
    {
        if (attributes.Count == 0)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteAttributes(writer, attributes);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteAttributes(Utf8JsonWriter writer, RepeatedField<KeyValue> attributes)
    {
        writer.WriteStartObject();
        foreach (var attribute in attributes)
        {
            var key = attribute.Key;
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (attribute.Value == null)
            {
                writer.WriteNull(key);
                continue;
            }

            WriteAnyValue(writer, key, attribute.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteAnyValue(Utf8JsonWriter writer, string key, AnyValue value)
    {
        switch (value.ValueCase)
        {
            case AnyValue.ValueOneofCase.StringValue:
                writer.WriteString(key, value.StringValue);
                break;
            case AnyValue.ValueOneofCase.BoolValue:
                writer.WriteBoolean(key, value.BoolValue);
                break;
            case AnyValue.ValueOneofCase.IntValue:
                writer.WriteNumber(key, value.IntValue);
                break;
            case AnyValue.ValueOneofCase.DoubleValue:
                writer.WriteNumber(key, value.DoubleValue);
                break;
            case AnyValue.ValueOneofCase.BytesValue:
                writer.WriteString(key, Convert.ToBase64String(value.BytesValue.ToByteArray()));
                break;
            case AnyValue.ValueOneofCase.ArrayValue:
                writer.WriteStartArray(key);
                foreach (var item in value.ArrayValue.Values)
                {
                    WriteItem(writer, item);
                }

                writer.WriteEndArray();
                break;
            case AnyValue.ValueOneofCase.KvlistValue:
                writer.WriteStartObject(key);
                foreach (var attribute in value.KvlistValue.Values)
                {
                    if (!string.IsNullOrEmpty(attribute.Key))
                    {
                        if (attribute.Value == null)
                        {
                            writer.WriteNull(attribute.Key);
                        }
                        else
                        {
                            WriteAnyValue(writer, attribute.Key, attribute.Value);
                        }
                    }
                }

                writer.WriteEndObject();
                break;
            default:
                writer.WriteNull(key);
                break;
        }
    }

    private static void WriteItem(Utf8JsonWriter writer, AnyValue value)
    {
        switch (value.ValueCase)
        {
            case AnyValue.ValueOneofCase.StringValue:
                writer.WriteStringValue(value.StringValue);
                break;
            case AnyValue.ValueOneofCase.BoolValue:
                writer.WriteBooleanValue(value.BoolValue);
                break;
            case AnyValue.ValueOneofCase.IntValue:
                writer.WriteNumberValue(value.IntValue);
                break;
            case AnyValue.ValueOneofCase.DoubleValue:
                writer.WriteNumberValue(value.DoubleValue);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>
    /// span 事件列表 → JSON 数组字符串；空返回 null（与 JSON parser 的 GetRawText 形状一致）.
    /// </summary>
    private static string? EventsToJson(RepeatedField<Span.Types.Event> events)
    {
        if (events.Count == 0)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var spanEvent in events)
            {
                writer.WriteStartObject();
                writer.WriteString("timeUnixNano", spanEvent.TimeUnixNano.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.WriteString("name", spanEvent.Name);
                if (spanEvent.Attributes.Count > 0)
                {
                    writer.WritePropertyName("attributes");
                    WriteAttributes(writer, spanEvent.Attributes);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// trace/span id（bytes）→ 小写 hex（与 OTLP JSON 字符串形态一致）；空返回 null.
    /// </summary>
    private static string? BytesToHex(ByteString bytes)
    {
        return bytes.IsEmpty ? null : Convert.ToHexString(bytes.ToByteArray()).ToLowerInvariant();
    }

    /// <summary>
    /// AnyValue → 字符串（正文场景：字符串直取，其余值取 JSON 文本）；空返回空串.
    /// </summary>
    private static string AnyValueToString(AnyValue? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        return value.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => value.StringValue,
            AnyValue.ValueOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            AnyValue.ValueOneofCase.IntValue => value.IntValue.ToString(),
            AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    private static string SeverityText(OtLogs.SeverityNumber severityNumber)
    {
        return (int)severityNumber switch
        {
            <= 4 => "TRACE",
            <= 8 => "DEBUG",
            <= 12 => "INFO",
            <= 16 => "WARN",
            <= 20 => "ERROR",
            _ => "FATAL",
        };
    }
}
