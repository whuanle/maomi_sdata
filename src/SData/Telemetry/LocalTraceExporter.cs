using System.Diagnostics;
using System.Text.Json;
using OpenTelemetry;
using SData.Infra;
using SData.Store;
using SData.Store.Models;

namespace SData;

/// <summary>
/// 自身链路固定写入本机缓冲，不经过网络接收端.
/// </summary>
public sealed class LocalTraceExporter(IIngestBuffer buffer) : BaseExporter<Activity>
{
    /// <inheritdoc/>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        using var suppression = SuppressInstrumentationScope.Begin();
        foreach (var activity in batch)
        {
            buffer.TryEnqueueSpan(new SpanRecord
            {
                TraceId = activity.TraceId.ToHexString(),
                SpanId = activity.SpanId.ToHexString(),
                ParentSpanId = activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
                Timestamp = new DateTimeOffset(activity.StartTimeUtc),
                DurationUs = activity.Duration.Ticks / 10,
                Service = AppConst.ServiceName,
                Name = activity.DisplayName,
                Kind = activity.Kind.ToString().ToLowerInvariant(),
                Status = activity.Status == ActivityStatusCode.Error || activity.GetTagItem("error") is true
                    ? "error" : activity.Status == ActivityStatusCode.Ok ? "ok" : "unset",
                AttributesJson = JsonSerializer.Serialize(activity.TagObjects.ToDictionary(x => x.Key, x => x.Value)),
                EventsJson = JsonSerializer.Serialize(activity.Events.Select(x => new
                {
                    name = x.Name,
                    timestamp = x.Timestamp,
                    attributes = x.Tags.ToDictionary(tag => tag.Key, tag => tag.Value),
                })),
                ResourceJson = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["service.name"] = AppConst.ServiceName,
                    ["service.instance.id"] = Environment.MachineName,
                }),
            });
        }

        // 缓冲满时沿用接入缓冲的丢弃计数，不重试整批造成重复.
        return ExportResult.Success;
    }
}
