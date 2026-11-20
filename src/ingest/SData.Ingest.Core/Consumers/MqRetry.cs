using System.Collections.Concurrent;
using SData.Infra.Configuration;

namespace SData.Ingest.Consumers;

/// <summary>封顶指数退避；只有持续正常进展一分钟后才重置.</summary>
public static class MqRetry
{
    private static readonly ConcurrentDictionary<long, RetryEntry> Entries = new();

    public static int DelaySeconds(int attempt, int initial = 5, int maximum = 60)
        => (int)Math.Min(Math.Clamp(maximum, 1, 3600), Math.Clamp(initial, 1, 3600) * Math.Pow(2, Math.Clamp(attempt - 1, 0, 20)));

    public static void Progress(long id, DateTimeOffset? observedAt = null)
    {
        if (!Entries.TryGetValue(id, out var entry)) return;
        if (!MqRuntime.IsHealthy(id))
        {
            lock (entry) entry.HealthySince = null;
            return;
        }
        lock (entry)
        {
            var now = observedAt ?? DateTimeOffset.UtcNow;
            if (entry.HealthySince == null) entry.HealthySince = now;
            if (now - entry.HealthySince >= TimeSpan.FromMinutes(1)) entry.Attempt = 0;
        }
    }

    public static async Task WaitAsync(MqInstanceOptions instance, CancellationToken token)
    {
        var entry = Entries.GetOrAdd(instance.Id, _ => new RetryEntry());
        int attempt;
        lock (entry)
        {
            entry.HealthySince = null;
            attempt = entry.Attempt = Math.Min(entry.Attempt + 1, 100);
        }

        var seconds = DelaySeconds(attempt, instance.RetryInitialSeconds, instance.RetryMaxSeconds);
        MqRuntime.RetryScheduled(instance.Id, attempt, DateTimeOffset.UtcNow.AddSeconds(seconds));
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); }
        finally { MqRuntime.RetryFinished(instance.Id); }
    }

    public static void Remove(long id) => Entries.TryRemove(id, out _);

    private sealed class RetryEntry
    {
        public int Attempt;
        public DateTimeOffset? HealthySince;
    }
}
