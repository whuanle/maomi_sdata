using System.Collections.Concurrent;

namespace SData.Ingest;

/// <summary>有界合并事件，不保存载荷；通知故障不阻塞消费.</summary>
public record MqFailureEvent(long InstanceId, string Topic, string Stage, string Code, string Reason, DateTimeOffset At, long Count = 1);

public static class MqFailureEvents
{
    private static readonly ConcurrentDictionary<(long, string), MqFailureEvent> Pending = new();
    private static readonly object Gate = new();

    public static void Publish(MqFailureEvent item)
    {
        lock (Gate)
        {
            var key = (item.InstanceId, item.Topic);
            if (Pending.TryGetValue(key, out var previous))
                Pending[key] = item with { Count = previous.Count + item.Count };
            else if (Pending.Count < 2048) Pending[key] = item;
        }
    }

    public static IReadOnlyList<MqFailureEvent> Drain(int maximum = 100)
    {
        lock (Gate)
        {
            var result = new List<MqFailureEvent>();
            foreach (var key in Pending.Keys.Take(maximum))
                if (Pending.TryRemove(key, out var item)) result.Add(item);
            return result;
        }
    }
}
