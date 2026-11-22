using System.Text.Json;
using SData.Store.Models;

namespace SData.Ingest.Services;

/// <summary>
/// OTLP/HTTP(JSON) logs 解析：resourceLogs → LogRecord 列表.
/// </summary>
public static class OtlpLogParser
{
    /// <summary>
    /// 解析 OTLP logs JSON.
    /// </summary>
    /// <param name="json">请求体.</param>
    /// <returns>日志记录.</returns>
    public static List<LogRecord> Parse(string json)
    {
        var records = new List<LogRecord>();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("resourceLogs", out var resourceLogs))
        {
            return records;
        }

        foreach (var resourceLog in resourceLogs.EnumerateArray())
        {
            var resourceJson = resourceLog.TryGetProperty("resource", out var resource) && resource.TryGetProperty("attributes", out var resourceAttributes)
                ? OtlpJson.AttributesToJson(resourceAttributes)
                : null;
            var service = OtlpJson.ExtractServiceName(resourceJson);

            if (!resourceLog.TryGetProperty("scopeLogs", out var scopeLogs))
            {
                continue;
            }

            foreach (var scopeLog in scopeLogs.EnumerateArray())
            {
                if (!scopeLog.TryGetProperty("logRecords", out var logRecords))
                {
                    continue;
                }

                foreach (var logRecord in logRecords.EnumerateArray())
                {
                    var timestampMs = OtlpJson.UnixNanoToMs(GetProperty(logRecord, "timeUnixNano", "observedTimeUnixNano")) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var severityText = logRecord.TryGetProperty("severityText", out var severityTextElement) ? severityTextElement.GetString() : null;
                    if (string.IsNullOrEmpty(severityText) && logRecord.TryGetProperty("severityNumber", out var severityNumber))
                    {
                        severityText = SeverityText(severityNumber);
                    }

                    records.Add(new LogRecord
                    {
                        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs),
                        Service = service,
                        Severity = severityText ?? "INFO",
                        Body = GetBody(logRecord),
                        TraceId = logRecord.TryGetProperty("traceId", out var traceId) ? traceId.GetString() : null,
                        SpanId = logRecord.TryGetProperty("spanId", out var spanId) ? spanId.GetString() : null,
                        AttributesJson = logRecord.TryGetProperty("attributes", out var attributes) ? OtlpJson.AttributesToJson(attributes) : null,
                        ResourceJson = resourceJson,
                    });
                }
            }
        }

        return records;
    }

    private static JsonElement GetProperty(JsonElement element, string name1, string? name2 = null)
    {
        if (element.TryGetProperty(name1, out var value))
        {
            return value;
        }

        if (name2 != null && element.TryGetProperty(name2, out var value2))
        {
            return value2;
        }

        return default;
    }

    private static string GetBody(JsonElement logRecord)
    {
        if (logRecord.TryGetProperty("body", out var body))
        {
            if (body.TryGetProperty("stringValue", out var stringValue))
            {
                return stringValue.GetString() ?? string.Empty;
            }

            return body.GetRawText();
        }

        return string.Empty;
    }

    private static string SeverityText(JsonElement severityNumber)
    {
        if (!severityNumber.TryGetInt32(out var number))
        {
            return "INFO";
        }

        return number switch
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
