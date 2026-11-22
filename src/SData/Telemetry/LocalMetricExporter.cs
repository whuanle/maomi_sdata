using System.Text.Json;
using System.Collections.Concurrent;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using SData.Infra;
using SData.Store;
using SData.Store.Models;

namespace SData;

/// <summary>
/// 自身指标固定写入本机缓冲；直方图按现有存储语义保留 count/sum.
/// </summary>
public sealed class LocalMetricExporter(IIngestBuffer buffer) : BaseExporter<Metric>
{
    private readonly ConcurrentDictionary<string, string> _labelsJsonCache = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<Metric> batch)
    {
        using var suppression = SuppressInstrumentationScope.Begin();
        foreach (var metric in batch)
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var tags = new List<(string Key, object? Value)>();
                foreach (var tag in point.Tags)
                {
                    tags.Add((tag.Key, tag.Value));
                }
                var tagsKeyBuilder = new StringBuilder(tags.Count * 16);
                foreach (var tag in tags)
                {
                    tagsKeyBuilder.Append(tag.Key).Append('=').Append(tag.Value).Append(';');
                }
                var tagsKey = tagsKeyBuilder.ToString();
                var labelsJson = _labelsJsonCache.GetOrAdd(tagsKey, _ =>
                {
                    var labels = new Dictionary<string, object?>();
                    foreach (var tag in tags)
                    {
                        labels[tag.Key] = tag.Value;
                    }

                    labels["service"] = AppConst.ServiceName;
                    labels["service.name"] = AppConst.ServiceName;
                    labels["service.instance.id"] = Environment.MachineName;
                    return JsonSerializer.Serialize(labels);
                });
                if (metric.MetricType.IsHistogram())
                {
                    Enqueue(metric.Name + ".count", point.GetHistogramCount(), "histogram", point.EndTime, labelsJson);
                    Enqueue(metric.Name + ".sum", point.GetHistogramSum(), "histogram", point.EndTime, labelsJson);
                }
                else if (metric.MetricType.IsSum())
                {
                    var value = metric.MetricType.IsLong() ? point.GetSumLong() : point.GetSumDouble();
                    Enqueue(metric.Name, value, "sum", point.EndTime, labelsJson);
                }
                else if (metric.MetricType.IsGauge())
                {
                    var value = metric.MetricType.IsLong() ? point.GetGaugeLastValueLong() : point.GetGaugeLastValueDouble();
                    Enqueue(metric.Name, value, "gauge", point.EndTime, labelsJson);
                }
            }
        }

        return ExportResult.Success;
    }

    private void Enqueue(string name, double value, string kind, DateTimeOffset timestamp, string labelsJson)
    {
        buffer.TryEnqueueMetric(new MetricPointRecord
        {
            Name = name,
            Value = value,
            Kind = kind,
            Timestamp = timestamp,
            LabelsJson = labelsJson,
        });
    }
}
