using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SData.Database;
using SData.Settings;
using SData.Infra.Configuration;

namespace SData.Alerts;

/// <summary>独立发送循环，有限重试；网络失败不占用查询执行槽.</summary>
public class AlertDeliveryWorker(IServiceScopeFactory scopes, TimeProvider clock, SystemOptions options, ILogger<AlertDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!options.Alerts.Enabled) continue;
            try { await SendNextAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Alert delivery failed ({ErrorType})", ex.GetType().Name); }
        }
    }

    public async Task SendNextAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var delivery = await db.AlertDeliveries.Where(x => x.Status == "pending" && x.NextAttempt <= now
                && !db.AlertDeliveries.Any(previous => previous.RuleId == x.RuleId && previous.ChannelId == x.ChannelId
                    && previous.Id < x.Id && previous.Status == "pending"))
            .OrderBy(x => x.Id).FirstOrDefaultAsync(token);
        if (delivery == null) return;
        var valid = await db.AlertRules.AsNoTracking().AnyAsync(x => x.Id == delivery.RuleId && x.Enabled && x.Version == delivery.Version, token);
        if (!valid)
        {
            delivery.Status = "cancelled";
            delivery.Result = "规则已停用、删除或更新";
            await db.SaveChangesAsync(token);
            return;
        }

        delivery.Attempts++;
        // 发送前持久化尝试次数，崩溃重启也不会无限重试。
        if (delivery.Attempts > 3)
        {
            delivery.Status = "failed";
            delivery.Result = "重试次数已耗尽";
            await db.SaveChangesAsync(token);
            return;
        }

        delivery.NextAttempt = now + 30 * delivery.Attempts;
        await db.SaveChangesAsync(token);
        NotificationSendResult result;
        try
        {
            result = await scope.ServiceProvider.GetRequiredService<INotificationSender>().SendAsync(delivery.ChannelId,
                new NotificationMessage { Title = delivery.Title, Body = delivery.Body, Link = delivery.Link,
                    Level = delivery.Kind == "resolved" ? "info" : "warning", Time = DateTimeOffset.FromUnixTimeSeconds(delivery.WindowEnd) }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Alert {RuleId} notification failed ({ErrorType})", delivery.RuleId, ex.GetType().Name);
            result = new NotificationSendResult(false, "通知发送失败");
        }

        delivery.Status = result.Success ? "sent" : delivery.Attempts >= 3 ? "failed" : "pending";
        delivery.Result = result.Success ? "发送成功" : "发送失败，请检查通知渠道";
        await db.SaveChangesAsync(token);
    }
}
