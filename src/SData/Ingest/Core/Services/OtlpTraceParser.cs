using System.Text.Json;
using SData.Store.Models;

namespace SData.Ingest.Services;

/// <summary>
/// OTLP/HTTP(JSON) traces 解析：resourceSpans → SpanRecord 列表.
/// </summary>
public static class OtlpTraceParser
{
    private static readonly string[] Kinds = { "unspecified", "internal", "server", "client", "producer", "consumer" };

    /// <summary>
    /// 解析 OTLP traces JSON.
    /// </summary>
    /// <param name="json">请求体.</param>
    /// <returns>span 记录.</returns>
    public static List<SpanRecord> Parse(string json)
    {
        var records = new List<SpanRecord>();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("resourceSpans", out var resourceSpans))
        {
            return records;
        }

        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            var resourceJson = resourceSpan.TryGetProperty("resource", out var resource) && resource.TryGetProperty("attributes", out var resourceAttributes)
                ? OtlpJson.AttributesToJson(resourceAttributes)
                : null;
            var service = OtlpJson.ExtractServiceName(resourceJson);

            if (!resourceSpan.TryGetProperty("scopeSpans", out var scopeSpans))
            {
                continue;
            }

            foreach (var scopeSpan in scopeSpans.EnumerateArray())
            {
                if (!scopeSpan.TryGetProperty("spans", out var spans))
                {
                    continue;
                }

                foreach (var span in spans.EnumerateArray())
                {
                    var startUs = OtlpJson.UnixNanoToUs(Get(span, "startTimeUnixNano")) ?? (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks / 10;
                    var endUs = OtlpJson.UnixNanoToUs(Get(span, "endTimeUnixNano")) ?? startUs;
                    var kind = span.TryGetProperty("kind", out var kindElement) && kindElement.TryGetInt32(out var kindNumber)
                        ? Kinds[Math.Clamp(kindNumber, 0, Kinds.Length - 1)]
                        : "internal";
                    var status = "unset";
                    if (span.TryGetProperty("status", out var statusElement) && statusElement.TryGetProperty("code", out var statusCode) && statusCode.TryGetInt32(out var statusCodeNumber))
                    {
                        status = statusCodeNumber switch { 1 => "ok", 2 => "error", _ => "unset" };
                    }

                    var eventsJson = (string?)null;
                    if (span.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array && events.GetArrayLength() > 0)
                    {
                        eventsJson = events.GetRawText();
                    }

                    records.Add(new SpanRecord
                    {
                        TraceId = span.TryGetProperty("traceId", out var traceId) ? traceId.GetString() ?? string.Empty : string.Empty,
                        SpanId = span.TryGetProperty("spanId", out var spanId) ? spanId.GetString() ?? string.Empty : string.Empty,
                        ParentSpanId = span.TryGetProperty("parentSpanId", out var parentSpanId) ? parentSpanId.GetString() : null,
                        Timestamp = DateTimeOffset.UnixEpoch.AddTicks(startUs * 10),
                        DurationUs = Math.Max(0, endUs - startUs),
                        Service = service,
                        Name = span.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                        Kind = kind,
                        Status = status,
                        AttributesJson = span.TryGetProperty("attributes", out var attributes) ? OtlpJson.AttributesToJson(attributes) : null,
                        EventsJson = eventsJson,
                        ResourceJson = resourceJson,
                    });
                }
            }
        }

        return records;
    }

    private static JsonElement Get(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) ? value : default;
    }
}
