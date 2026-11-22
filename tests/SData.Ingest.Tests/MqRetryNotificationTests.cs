using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Configuration;
using SData.Infra.Services;
using SData.Ingest.Consumers;
using SData.Settings;
using Xunit;

namespace SData.Ingest.Tests;

[Collection("mq-runtime")]
public class MqRetryNotificationTests
{
    [Fact]
    public void BackoffCapsAndRemainsAtFixedMaximum()
        => Assert.Equal(new[] { 5, 10, 20, 40, 60, 60, 60, 60 },
            Enumerable.Range(1, 8).Select(attempt => MqRetry.DelaySeconds(attempt)));

    [Fact]
    public async Task CancelledRetryPreservesBackoffAndStableRecoveryResetsIt()
    {
        var id = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        var instance = new MqInstanceOptions { Id = id };
        MqRuntime.Start(id, [("logs", "exact")]);
        try
        {
            using var stop = new CancellationTokenSource();
            var pending = MqRetry.WaitAsync(instance, stop.Token);
            var before = MqRuntime.Snapshot().Single(x => x.InstanceId == id);
            Assert.Equal("retrying", before.State);
            Assert.Equal(1, before.RetryAttempt);
            Assert.NotNull(before.NextRetryAt);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Null(MqRuntime.Snapshot().Single(x => x.InstanceId == id).NextRetryAt);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MqRetry.WaitAsync(instance, stop.Token));
            Assert.Equal(2, MqRuntime.Snapshot().Single(x => x.InstanceId == id).RetryAttempt);
            MqRuntime.Connected(id);
            var now = DateTimeOffset.UtcNow;
            MqRetry.Progress(id, now);
            MqRetry.Progress(id, now.AddSeconds(61));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MqRetry.WaitAsync(instance, stop.Token));
            Assert.Equal(1, MqRuntime.Snapshot().Single(x => x.InstanceId == id).RetryAttempt);
        }
        finally
        {
            MqRetry.Remove(id);
            MqRuntime.Remove(id);
        }
    }

    [Fact]
    public void ReserveCountsEveryFailureWithinWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new MqNotificationStateEntity();
        Assert.True(MqNotificationWorker.Reserve(state, now, 10, 1));
        Assert.False(MqNotificationWorker.Reserve(state, now.AddSeconds(1), 10, 25));
        Assert.Equal(25, state.SuppressedCount);
        Assert.True(MqNotificationWorker.Reserve(state, now.AddMinutes(10), 10, 1));
    }

    [Fact]
    public async Task QueueCooldownSurvivesWorkerRestartAndStageChanges()
    {
        var id = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        var path = Path.Combine(Path.GetTempPath(), "mq-notifications-" + Guid.NewGuid().ToString("N") + ".db");
        var settings = new Settings(id);
        var sender = new Sender();
        using var services = new ServiceCollection()
            .AddDbContext<DatabaseContext>(builder => builder.UseSqlite("Data Source=" + path))
            .AddSingleton<INotificationSender>(sender).BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            await db.Database.EnsureCreatedAsync();
            db.NotificationChannels.Add(new NotificationChannelEntity
            {
                Id = 1, Enabled = true, Name = "fake", Type = "webhook", ProtectedConfig = "unused",
            });
            await db.SaveChangesAsync();
        }
        MqNotificationWorker Worker() => new(services.GetRequiredService<IServiceScopeFactory>(),
            settings, new DefaultIdProvider(1), NullLogger<MqNotificationWorker>.Instance);
        MqFailureEvents.Publish(new(id, "logs-a", "parse", "json_invalid", "JSON 格式错误", DateTimeOffset.UtcNow));
        using (var worker = Worker()) await worker.RunOnceAsync(default);
        Assert.Single(sender.Messages);
        // A failed delivery still consumes this window; restarting does not reopen it.
        MqFailureEvents.Publish(new(id, "logs-a", "confirm", "confirmation_failed", "确认失败", DateTimeOffset.UtcNow, 20));
        using (var worker = Worker()) await worker.RunOnceAsync(default);
        Assert.Single(sender.Messages);
        MqFailureEvents.Publish(new(id, "logs-b", "parse", "json_invalid", "JSON 格式错误", DateTimeOffset.UtcNow));
        using (var worker = Worker()) await worker.RunOnceAsync(default);
        Assert.Equal(2, sender.Messages.Count);
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            var state = await db.MqNotificationStates.SingleAsync(x => x.InstanceId == id && x.Topic == "logs-a");
            Assert.Equal(20, state.SuppressedCount);
            Assert.Equal("failed", state.DeliveryStatus);
            // Expire the window: the next failure carries the suppressed summary.
            state.SuppressedUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        MqFailureEvents.Publish(new(id, "logs-a", "parse", "json_invalid", "JSON 格式错误", DateTimeOffset.UtcNow));
        using (var worker = Worker()) await worker.RunOnceAsync(default);
        Assert.Equal(3, sender.Messages.Count);
        Assert.Contains("20", sender.Messages.Last().Body);
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            var channel = await db.NotificationChannels.SingleAsync();
            channel.Enabled = false;
            await db.SaveChangesAsync();
        }
        MqFailureEvents.Publish(new(id, "logs-c", "parse", "json_invalid", "JSON 格式错误", DateTimeOffset.UtcNow));
        using (var worker = Worker()) await worker.RunOnceAsync(default);
        Assert.Equal(3, sender.Messages.Count);
    }

    [Fact]
    public async Task ImmediateRetryCoalescesRequestsAndNeverOverlapsConsumers()
    {
        var id = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        var settings = new Settings(id);
        settings.Instance.Type = "kafka";
        settings.Instance.BootstrapServers = "unused";
        settings.Instance.Topics = [new() { Name = "logs" }];
        using var manager = new MqConsumerManagerService(settings,
            new SData.Store.IngestBuffer(new SystemOptions()), new MqLogDecoder(new SData.Ingest.Services.OtlpProtoParserService()),
            NullLogger<MqConsumerManagerService>.Instance);
        var entries = (Dictionary<string, Func<MqInstanceOptions, CancellationToken, Task>>)typeof(MqConsumerManagerService)
            .GetField("_consumerEntries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(manager)!;
        var launched = 0;
        var active = 0;
        var maximum = 0;
        entries["kafka"] = async (_, token) =>
        {
            Interlocked.Increment(ref launched);
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Interlocked.Decrement(ref active); }
        };
        try
        {
            Assert.False(manager.RequestRetry(id + 1));
            await manager.ReconcileAsync();
            await WaitUntilAsync(() => Volatile.Read(ref launched) == 1);
            Parallel.For(0, 20, _ => Assert.True(manager.RequestRetry(id)));
            await Task.WhenAll(manager.ReconcileAsync(), manager.ReconcileAsync());
            await WaitUntilAsync(() => Volatile.Read(ref launched) == 2);
            Assert.Equal(2, launched);
            Assert.Equal(1, maximum);
        }
        finally
        {
            settings.Instance.Enabled = false;
            await manager.ReconcileAsync();
            MqRuntime.Remove(id);
            MqRetry.Remove(id);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private sealed class Sender : INotificationSender
    {
        public List<NotificationMessage> Messages { get; } = [];
        public Task<NotificationSendResult> SendAsync(long channelId, NotificationMessage message, CancellationToken token)
        {
            Messages.Add(message);
            return Task.FromResult(new NotificationSendResult(false, "fake delivery failure"));
        }
    }

    private sealed class Settings(long id) : ISystemSettingsProvider
    {
        public MqInstanceOptions Instance { get; } = new() { Id = id, Name = "fake consumer", Enabled = true, AlertsEnabled = true, NotificationChannelIds = ["1"] };
        public List<MqInstanceOptions> GetMqInstances() => [Instance];
        public DuckDbSettings GetDuckDb() => new();
        public RuntimeSettings GetRuntime() => new();
        public S3Options GetS3() => new();
        public RetentionPlanOptions GetRetention() => new();
        public CompressionOptions GetCompression() => new();
        public OtlpOptions GetOtlp() => new();
        public Task SaveAsync(string key, string json, CancellationToken token) => throw new NotSupportedException();
    }
}
