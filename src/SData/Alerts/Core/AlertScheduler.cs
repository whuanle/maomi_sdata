using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Exceptions;
using SData.Infra.Services;
using SData.Settings;
using SData.Store;
using SData.Store.Models;

namespace SData.Alerts;

/// <summary>串行、公平轮转；只评估最新完整窗口，不补跑积压窗口（告警开关与待投递上限取运行参数，每轮读取）.</summary>
public class AlertScheduler(IServiceScopeFactory scopes, AlertExecutionGate gate, ISystemSettingsProvider settings, TimeProvider clock, ILogger<AlertScheduler> logger) : BackgroundService
{
    private long _cursor;
    private long _nextCleanup;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            if (!settings.GetRuntime().Alerts.Enabled) continue;
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Alert sweep failed ({ErrorType})", ex.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SweepAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var candidates = await db.AlertRules.AsNoTracking().Where(x => x.Enabled && x.NextAttempt <= now)
            .OrderBy(x => x.Id).Take(500).ToListAsync(token);
        foreach (var snapshot in candidates.OrderBy(x => x.Id <= _cursor).ThenBy(x => x.Id))
        {
            var window = AlertState.WindowEnd(clock.GetUtcNow(), snapshot.WindowMinutes, snapshot.DelayMinutes);
            if (window <= snapshot.LastWindow) continue;
            var executor = scope.ServiceProvider.GetRequiredService<IAnalyticsExecutor>();
            AlertCountResult? result = null;
            string? error = null;
            try
            {
                var end = DateTimeOffset.FromUnixTimeSeconds(window);
                var parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(snapshot.Parameters) ?? new();
                result = await gate.ExecuteAsync(executor, new AlertCountQuery(snapshot.Domain, snapshot.Condition, parameters,
                    end.AddMinutes(-snapshot.WindowMinutes), end, Math.Max(1, snapshot.Threshold + 1)), token);
            }
            catch (BusinessException ex) when (ex.StatusCode == 429) { break; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                error = ex is OperationCanceledException ? "告警查询超时" : "告警评估失败，请检查条件与热数据覆盖范围";
                logger.LogWarning("Alert {RuleId} evaluation failed ({ErrorType})", snapshot.Id, ex.GetType().Name);
            }

            _cursor = snapshot.Id;
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var rule = await db.AlertRules.SingleOrDefaultAsync(x => x.Id == snapshot.Id, token);
            if (rule == null || !rule.Enabled || rule.Version != snapshot.Version || rule.LastWindow >= window) continue;
            var previousFiring = rule.Firing;
            var previousHits = rule.Hits;
            var kind = result == null ? null : AlertState.Apply(rule, window, result.Count);
            if (result == null)
            {
                rule.LastWindow = window;
                rule.Status = "error";
                rule.Error = error!;
                rule.Hits = 0;
                rule.Failures++;
                rule.NextAttempt = now + Math.Min(1800, 60L * (1L << Math.Min(rule.Failures, 5)));
            }

            if (kind != null)
            {
                var channels = JsonSerializer.Deserialize<List<string>>(rule.Channels) ?? new();
                var pending = await db.AlertDeliveries.CountAsync(x => x.Status == "pending", token);
                if (pending + channels.Count > Math.Clamp(settings.GetRuntime().Alerts.MaxPendingDeliveries, 1, 10000))
                {
                    // 不丢失状态转换通知；下一完整窗口重试该转换。
                    rule.Firing = previousFiring;
                    rule.Hits = previousHits;
                    rule.Status = "error";
                    rule.Error = "通知队列已满，等待下个窗口重试";
                }
                else
                {
                    var ids = scope.ServiceProvider.GetRequiredService<IIdProvider>();
                    foreach (var channel in channels)
                    {
                        var countText = result!.LowerBound ? "≥" + result.Count : result.Count.ToString();
                        db.AlertDeliveries.Add(new AlertDeliveryEntity
                        {
                            Id = ids.GetLongId(), RuleId = rule.Id, Version = rule.Version, WindowEnd = window,
                            ChannelId = long.Parse(channel), Kind = kind,
                            Title = (kind == "resolved" ? "告警恢复：" : "搜索告警：") + rule.Name,
                            Body = $"规则：{rule.Name}\n窗口结束：{DateTimeOffset.FromUnixTimeSeconds(window):u}\n匹配次数：{countText}\n{rule.Note}",
                        });
                    }
                }
            }

            db.AlertEvaluations.Add(new AlertEvaluationEntity
            {
                Id = scope.ServiceProvider.GetRequiredService<IIdProvider>().GetLongId(),
                RuleId = rule.Id, Version = rule.Version, WindowEnd = window,
                Count = result?.Count ?? 0, LowerBound = result?.LowerBound ?? false,
                Status = rule.Status, Error = rule.Error,
            });
            try { await db.SaveChangesAsync(token); await transaction.CommitAsync(token); }
            catch (DbUpdateConcurrencyException) { /* 编辑先完成时丢弃旧版本评估。 */ }
        }

        // 每小时清理一次，不在 10 秒调度路径反复扫描历史。
        if (now < _nextCleanup) return;
        _nextCleanup = now + 3600;
        // 硬删除仅用于有界运行历史，不修改业务规则的软删除语义。
        db.ChangeTracker.Clear();
        var cutoff = now - 7 * 86400;
        await db.AlertEvaluations.Where(x => x.WindowEnd < cutoff).ExecuteDeleteAsync(token);
        await db.AlertDeliveries.Where(x => x.WindowEnd < cutoff && x.Status != "pending").ExecuteDeleteAsync(token);
    }
}
