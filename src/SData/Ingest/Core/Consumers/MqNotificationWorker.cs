using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Services;
using SData.Settings;

namespace SData.Ingest.Consumers;

/// <summary>串行有界投递，先持久预留冷却窗口，发送失败也不绕过预算.</summary>
public class MqNotificationWorker(IServiceScopeFactory scopes, ISystemSettingsProvider settings,
    IIdProvider ids, ILogger<MqNotificationWorker> logger) : BackgroundService
{
    public static bool Reserve(MqNotificationStateEntity state, DateTimeOffset now, int minutes, long count)
    {
        if (now < state.SuppressedUntil)
        {
            state.SuppressedCount += count;
            return false;
        }

        state.SuppressedUntil = now.AddMinutes(minutes);
        state.LastAttemptAt = now;
        state.DeliveryStatus = "sending";
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            foreach (var state in await db.MqNotificationStates.AsNoTracking().Take(4096).ToListAsync(stoppingToken))
                MqNotificationRuntime.Publish(state.InstanceId, state.Topic, state.ChannelId.ToString(), state.SuppressedUntil, state.SuppressedCount, state.DeliveryStatus == "sending" ? "unknown" : state.DeliveryStatus);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception exception) { logger.LogWarning(exception, "消费告警状态加载失败，后台继续重试"); }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "消费告警处理失败，本轮不阻塞消费"); }
        }
    }

    public async Task RunOnceAsync(CancellationToken token)
    {
        var instances = settings.GetMqInstances().Where(x => x.AlertsEnabled && x.Enabled).ToDictionary(x => x.Id);
        foreach (var failure in MqFailureEvents.Drain(100))
        {
            if (!instances.TryGetValue(failure.InstanceId, out var instance)) continue;
            foreach (var channel in instance.NotificationChannelIds.Distinct())
            {
                if (!long.TryParse(channel, out var channelId)) continue;
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
                var state = await db.MqNotificationStates.SingleOrDefaultAsync(x =>
                    x.InstanceId == instance.Id && x.Topic == failure.Topic && x.ChannelId == channelId, token);
                if (state == null)
                {
                    if (await db.MqNotificationStates.CountAsync(token) >= 4096)
                    {
                        MqNotificationRuntime.Publish(instance.Id, failure.Topic, channel, null, 0, "capacity");
                        continue;
                    }
                    state = new MqNotificationStateEntity { Id = ids.GetLongId(), InstanceId = instance.Id, Topic = failure.Topic, ChannelId = channelId };
                    db.MqNotificationStates.Add(state);
                }
                if (!await db.NotificationChannels.AnyAsync(x => x.Id == channelId && x.Enabled, token))
                {
                    state.DeliveryStatus = "disabled";
                    await db.SaveChangesAsync(token);
                    MqNotificationRuntime.Publish(instance.Id, failure.Topic, channel, state.SuppressedUntil, state.SuppressedCount, "disabled");
                    continue;
                }
                var suppressed = state.SuppressedCount;
                var send = Reserve(state, DateTimeOffset.UtcNow, Math.Clamp(instance.AlertCooldownMinutes, 1, 1440), failure.Count);
                state.LastFailureId = failure.Code;
                await db.SaveChangesAsync(token);
                if (!send)
                {
                    MqNotificationRuntime.Publish(instance.Id, failure.Topic, channel, state.SuppressedUntil, state.SuppressedCount, state.DeliveryStatus);
                    continue;
                }

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                try
                {
                    var sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
                    var result = await sender.SendAsync(channelId, new NotificationMessage
                    {
                        Title = "SData 消费失败：" + instance.Name,
                        Body = $"实例：{instance.Name}\n队列/主题：{(failure.Topic.Length == 0 ? "实例连接" : failure.Topic)}\n阶段：{failure.Stage}\n原因：{failure.Reason}\n时间：{failure.At:O}\n本批失败：{failure.Count}；上个窗口抑制：{suppressed}\n失败消息保持未确认并按配置退避重试；完整解析和落库前不会推进消费位置。",
                        Level = "error", Time = failure.At, Link = "/log-consumption",
                    }, deadline.Token);
                    state.DeliveryStatus = result.Success ? "sent" : "failed";
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { state.DeliveryStatus = "timeout"; }
                catch (Exception) when (!token.IsCancellationRequested) { state.DeliveryStatus = "failed"; }
                state.SuppressedCount = Math.Max(0, failure.Count - 1);
                await db.SaveChangesAsync(token);
                MqNotificationRuntime.Publish(instance.Id, failure.Topic, channel, state.SuppressedUntil, state.SuppressedCount, state.DeliveryStatus);
            }
        }
    }
}
