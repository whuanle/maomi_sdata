using System.Collections.Concurrent;

namespace SData.Ingest;

public record MqNotificationItem(long InstanceId, string Topic, string ChannelId, DateTimeOffset? SuppressedUntil, long SuppressedCount, string Status);

/// <summary>通知状态安全镜像；持久冷却以 SQLite 为准.</summary>
public static class MqNotificationRuntime
{
    private static readonly ConcurrentDictionary<(long, string, string), MqNotificationItem> Items = new();

    public static void Publish(long id, string topic, string channel, DateTimeOffset? until, long count, string status)
    {
        if (Items.Count >= 4096 && !Items.ContainsKey((id, topic, channel))) return;
        Items[(id, topic, channel)] = new(id, topic, channel, until, count, status);
    }

    public static IReadOnlyList<MqNotificationItem> Snapshot() => Items.Values.ToList();
}
