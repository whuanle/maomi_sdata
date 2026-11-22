using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Alerts;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Configuration;
using SData.Infra.Exceptions;
using SData.Infra.Services;
using SData.Settings;
using SData.Settings.Services;
using SData.Store.Models;

namespace SData.Store.Tests;

public class AlertIntegrationTests
{
    [Fact]
    public async Task Save_Evaluate_Deduplicate_Resolve_AndDeliver()
    {
        using var harness = new Harness();
        var item = await harness.SaveAsync();
        Assert.NotNull(item.Id);
        Assert.Equal(1, await harness.CountSavedAsync());
        harness.Clock.Advance(300);
        harness.Executor.Count = 3;
        await harness.Scheduler.SweepAsync(default);
        await harness.Scheduler.SweepAsync(default);
        Assert.Equal(1, await harness.DeliveriesAsync());
        await harness.Worker.SendNextAsync(default);
        Assert.Single(harness.Sender.Messages);
        harness.Clock.Advance(300);
        await harness.Scheduler.SweepAsync(default);
        Assert.Equal(1, await harness.DeliveriesAsync());
        harness.Executor.Count = 0;
        harness.Clock.Advance(300);
        await harness.Scheduler.SweepAsync(default);
        await harness.Worker.SendNextAsync(default);
        Assert.Equal(2, harness.Sender.Messages.Count);
        Assert.Contains("恢复", harness.Sender.Messages[1].Title);
    }

    [Fact]
    public async Task Error_DoesNotResolve_AndOldVersionDeliveryIsCancelled()
    {
        using var harness = new Harness();
        var item = await harness.SaveAsync();
        harness.Executor.Count = 2;
        harness.Clock.Advance(300);
        await harness.Scheduler.SweepAsync(default);
        harness.Executor.Fail = true;
        harness.Clock.Advance(300);
        await harness.Scheduler.SweepAsync(default);
        using (var scope = harness.Services.CreateScope())
        {
            var rule = await scope.ServiceProvider.GetRequiredService<DatabaseContext>().AlertRules.SingleAsync();
            Assert.True(rule.Firing);
            Assert.Equal("error", rule.Status);
        }
        var changed = await harness.SaveAsync(item.Id, item.Version, enabled: false);
        Assert.Equal(2, changed.Version);
        await harness.Worker.SendNextAsync(default);
        Assert.Empty(harness.Sender.Messages);
        using var check = harness.Services.CreateScope();
        Assert.Equal("cancelled", (await check.ServiceProvider.GetRequiredService<DatabaseContext>().AlertDeliveries.SingleAsync()).Status);
    }

    [Fact]
    public async Task Save_RejectsVersionConflict_AndChannelSecretsNeverReturned()
    {
        using var harness = new Harness();
        var item = await harness.SaveAsync();
        await harness.SaveAsync(item.Id, item.Version);
        var error = await Assert.ThrowsAsync<BusinessException>(() => harness.SaveAsync(item.Id, item.Version));
        Assert.Equal(409, error.StatusCode);
        using var scope = harness.Services.CreateScope();
        var channels = await harness.Handlers(scope).Handle(new QueryAlertChannelsCommand(), default);
        Assert.Equal("test", Assert.Single(channels).Name);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(channels));
    }

    [Fact]
    public async Task Delivery_RetriesAreBounded()
    {
        using var harness = new Harness();
        await harness.SaveAsync();
        harness.Executor.Count = 2;
        harness.Clock.Advance(300);
        await harness.Scheduler.SweepAsync(default);
        harness.Sender.Success = false;
        for (var i = 0; i < 5; i++)
        {
            await harness.Worker.SendNextAsync(default);
            harness.Clock.Advance(300);
        }
        Assert.Equal(3, harness.Sender.Messages.Count);
        using var scope = harness.Services.CreateScope();
        Assert.Equal("failed", (await scope.ServiceProvider.GetRequiredService<DatabaseContext>().AlertDeliveries.SingleAsync()).Status);
    }

    [Fact]
    public async Task Gate_SharesSingleSlotWithPreview_AndReleasesAfterCancellation()
    {
        var options = new SystemOptions { Alerts = new() { QueryTimeoutSeconds = 1 } };
        var gate = new AlertExecutionGate(SystemSettingsProvider.FileOnly(options), TimeProvider.System);
        var executor = new FakeExecutor { Block = true };
        var query = new AlertCountQuery("logs", "", new Dictionary<string, object?>(), DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, 2);
        var first = gate.ExecuteAsync(executor, query, default);
        await executor.Started.Task;
        var busy = await Assert.ThrowsAsync<BusinessException>(() => gate.ExecuteAsync(executor, query, default));
        Assert.Equal(429, busy.StatusCode);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        executor.Block = false;
        await gate.ExecuteAsync(executor, query, default);
    }

    [Fact]
    public async Task QueueFull_IsBoundedAndVisible()
    {
        using var harness = new Harness();
        await harness.SetAlertsAsync(alerts => alerts.MaxPendingDeliveries = 1);
        await harness.SaveAsync();
        harness.Executor.Count = 2;
        harness.Clock.Advance(300);
        await harness.Scheduler.SweepAsync(default);
        harness.Executor.Count = 0;
        harness.Clock.Advance(300);
        await harness.Scheduler.SweepAsync(default);
        Assert.Equal(1, await harness.DeliveriesAsync());
        using var scope = harness.Services.CreateScope();
        var rule = await scope.ServiceProvider.GetRequiredService<DatabaseContext>().AlertRules.SingleAsync();
        Assert.Equal("error", rule.Status);
        Assert.True(rule.Firing);
    }

    [Fact]
    public async Task Delivery_RetryCannotOvertakeRecovery()
    {
        using var harness = new Harness();
        await harness.SaveAsync();
        harness.Executor.Count = 2;
        harness.Clock.Advance(300);
        await harness.Scheduler.SweepAsync(default);
        harness.Sender.Success = false;
        await harness.Worker.SendNextAsync(default);
        harness.Executor.Count = 0;
        harness.Clock.Advance(300);
        await harness.Scheduler.SweepAsync(default);
        // 延后旧通知，恢复通知不能越过它。
        using (var scope = harness.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            var first = await db.AlertDeliveries.OrderBy(x => x.Id).FirstAsync();
            first.NextAttempt = harness.Clock.GetUtcNow().ToUnixTimeSeconds() + 100;
            await db.SaveChangesAsync();
        }
        await harness.Worker.SendNextAsync(default);
        Assert.Single(harness.Sender.Messages);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-11-19T10:01:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class FakeExecutor : IAnalyticsExecutor
    {
        public long Count;
        public bool Fail;
        public bool Block;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AnalyticsQueryResult> ExecuteAsync(string sql, int rowLimit = 1000, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AnalyticsQueryResult> ExecuteSearchAsync(string sql, DateTimeOffset from, DateTimeOffset to, int rowLimit = 500, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<AlertCountResult> ExecuteAlertCountAsync(AlertCountQuery query, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Fail) throw new InvalidOperationException("secret remote failure");
            return new(Math.Min(Count, query.Cap), Count >= query.Cap);
        }
    }

    private sealed class Sender : INotificationSender
    {
        public bool Success = true;
        public List<NotificationMessage> Messages { get; } = new();
        public Task<NotificationSendResult> SendAsync(long channelId, NotificationMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.FromResult(new NotificationSendResult(Success, "secret remote response"));
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "alerts-" + Guid.NewGuid().ToString("N") + ".db");
        public readonly ServiceProvider Services;
        public readonly Clock Clock = new();
        public readonly FakeExecutor Executor = new();
        public readonly Sender Sender = new();
        public readonly SystemOptions Options = new();
        public readonly SystemSettingsProvider Settings;
        public readonly AlertExecutionGate Gate;
        public readonly AlertScheduler Scheduler;
        public readonly AlertDeliveryWorker Worker;

        public Harness()
        {
            Services = new ServiceCollection()
                .AddDbContext<DatabaseContext>(builder => builder.UseSqlite("Data Source=" + _path))
                .AddSingleton<IIdProvider>(new DefaultIdProvider(1))
                .AddSingleton<IAnalyticsExecutor>(Executor)
                .AddSingleton<INotificationSender>(Sender).BuildServiceProvider();
            Settings = new SystemSettingsProvider(Options, Services.GetRequiredService<IServiceScopeFactory>(), new DefaultIdProvider(1), NullLogger<SystemSettingsProvider>.Instance);
            Gate = new(Settings, Clock);
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            db.Database.EnsureCreated();
            db.NotificationChannels.Add(new NotificationChannelEntity { Id = 1, Name = "test", Type = "webhook", Enabled = true, ProtectedConfig = "secret" });
            db.SaveChanges();
            Scheduler = new(Services.GetRequiredService<IServiceScopeFactory>(), Gate, Settings, Clock, NullLogger<AlertScheduler>.Instance);
            Worker = new(Services.GetRequiredService<IServiceScopeFactory>(), Clock, Settings, NullLogger<AlertDeliveryWorker>.Instance);
        }

        public AlertHandlers Handlers(IServiceScope scope) => new(scope.ServiceProvider.GetRequiredService<DatabaseContext>(),
            Services.GetRequiredService<IIdProvider>(), Settings, Gate, Executor, Clock);

        /// <summary>在当前生效的运行参数上修改告警段，经设置提供器保存（验证热读，不直接改文件配置）.</summary>
        public Task SetAlertsAsync(Action<AlertOptions> change)
        {
            var runtime = System.Text.Json.JsonSerializer.Deserialize<RuntimeSettings>(System.Text.Json.JsonSerializer.Serialize(Settings.GetRuntime()))!;
            change(runtime.Alerts);
            return Settings.SaveAsync(SettingKeys.Runtime, System.Text.Json.JsonSerializer.Serialize(runtime, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), default);
        }

        public async Task<AlertItem> SaveAsync(string? id = null, long version = 0, bool enabled = true)
        {
            using var scope = Services.CreateScope();
            return await Handlers(scope).Handle(new SaveAlertCommand
            {
                Id = id, Version = version, Name = "errors", Domain = "logs", Definition = "{\"domain\":\"logs\"}",
                Condition = "severity = $p0", Parameters = new() { ["p0"] = "ERROR" }, Channels = ["1"],
                Enabled = enabled, SavedQueryId = id == null ? null : (await scope.ServiceProvider.GetRequiredService<DatabaseContext>().AlertRules.SingleAsync()).SavedQueryId.ToString(),
            }, default);
        }

        public async Task<int> CountSavedAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<DatabaseContext>().SavedQueries.CountAsync();
        }
        public async Task<int> DeliveriesAsync()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<DatabaseContext>().AlertDeliveries.CountAsync();
        }
        public void Dispose()
        {
            Services.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(_path);
        }
    }
}
