using System.Text.Json;
using SData.Store.Models;

namespace SData.Ingest.Services;

/// <summary>
/// OTLP/HTTP(JSON) metrics 解析：resourceMetrics → MetricPointRecord 列表.
/// gauge/sum 数据点直接入桶；histogram 仅记录 count/sum 两个派生序列.
/// </summary>
public static class OtlpMetricParser
{
    /// <summary>
    /// 解析 OTLP metrics JSON.
    /// </summary>
    /// <param name="json">请求体.</param>
    /// <returns>指标点.</returns>
    public static List<MetricPointRecord> Parse(string json)
    {
        var records = new List<MetricPointRecord>();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("resourceMetrics", out var resourceMetrics))
        {
            return records;
        }

        foreach (var resourceMetric in resourceMetrics.EnumerateArray())
        {
            var resourceJson = resourceMetric.TryGetProperty("resource", out var resource) && resource.TryGetProperty("attributes", out var resourceAttributes)
                ? OtlpJson.AttributesToJson(resourceAttributes)
                : null;

            if (!resourceMetric.TryGetProperty("scopeMetrics", out var scopeMetrics))
            {
                continue;
            }

            foreach (var scopeMetric in scopeMetrics.EnumerateArray())
            {
                if (!scopeMetric.TryGetProperty("metrics", out var metrics))
                {
                    continue;
                }

                foreach (var metric in metrics.EnumerateArray())
                {
                    var metricName = metric.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrEmpty(metricName))
                    {
                        continue;
                    }

                    if (metric.TryGetProperty("gauge", out var gauge) && gauge.TryGetProperty("dataPoints", out var gaugePoints))
                    {
                        ParsePoints(gaugePoints, metricName, "gauge", resourceJson, records);
                    }

                    if (metric.TryGetProperty("sum", out var sum) && sum.TryGetProperty("dataPoints", out var sumPoints))
                    {
                        ParsePoints(sumPoints, metricName, "sum", resourceJson, records);
                    }

                    if (metric.TryGetProperty("histogram", out var histogram) && histogram.TryGetProperty("dataPoints", out var histogramPoints))
                    {
                        ParseHistogram(histogramPoints, metricName, records);
                    }
                }
            }
        }

        return records;
    }

    private static void ParsePoints(JsonElement points, string metricName, string kind, string? resourceJson, List<MetricPointRecord> records)
    {
        foreach (var point in points.EnumerateArray())
        {
            var value = GetValue(point);
            if (value == null)
            {
                continue;
            }

            var timestampMs = OtlpJson.UnixNanoToMs(Get(point, "timeUnixNano")) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var labelsJson = point.TryGetProperty("attributes", out var attributes) ? OtlpJson.AttributesToJson(attributes) : null;
            if (labelsJson != null && resourceJson != null)
            {
                labelsJson = MergeService(labelsJson, resourceJson);
            }
            else if (labelsJson == null)
            {
                labelsJson = resourceJson == null ? "{}" : $"{{\"service\":{JsonSerializer.Serialize(OtlpJson.ExtractServiceName(resourceJson))}}}";
            }

            records.Add(new MetricPointRecord
            {
                Name = metricName,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs),
                Value = value.Value,
                LabelsJson = labelsJson,
                Kind = kind,
            });
        }
    }

    private static void ParseHistogram(JsonElement points, string metricName, List<MetricPointRecord> records)
    {
        foreach (var point in points.EnumerateArray())
        {
            var timestampMs = OtlpJson.UnixNanoToMs(Get(point, "timeUnixNano")) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var labelsJson = point.TryGetProperty("attributes", out var attributes) ? OtlpJson.AttributesToJson(attributes) ?? "{}" : "{}";
            if (point.TryGetProperty("count", out var count) && count.TryGetInt64(out var countValue))
            {
                records.Add(new MetricPointRecord
                {
                    Name = metricName + ".count",
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs),
                    Value = countValue,
                    LabelsJson = labelsJson,
                    Kind = "histogram",
                });
            }

            if (point.TryGetProperty("sum", out var sum) && sum.ValueKind == JsonValueKind.Number)
            {
                records.Add(new MetricPointRecord
                {
                    Name = metricName + ".sum",
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs),
                    Value = sum.GetDouble(),
                    LabelsJson = labelsJson,
                    Kind = "histogram",
                });
            }
        }
    }

    private static double? GetValue(JsonElement point)
    {
        if (point.TryGetProperty("asDouble", out var asDouble) && asDouble.ValueKind == JsonValueKind.Number)
        {
            return asDouble.GetDouble();
        }

        if (point.TryGetProperty("asInt", out var asInt))
        {
            if (asInt.ValueKind == JsonValueKind.Number && asInt.TryGetInt64(out var intValue))
            {
                return intValue;
            }

            if (asInt.ValueKind == JsonValueKind.String && long.TryParse(asInt.GetString(), out var intFromString))
            {
                return intFromString;
            }
        }

        return null;
    }

    private static string MergeService(string labelsJson, string resourceJson)
    {
        var service = OtlpJson.ExtractServiceName(resourceJson);
        return $"{{\"service\":{JsonSerializer.Serialize(service)},\"labels\":{labelsJson}}}";
    }

    private static JsonElement Get(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) ? value : default;
    }
}
