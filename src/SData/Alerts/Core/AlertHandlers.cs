using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Exceptions;
using SData.Infra.Services;
using SData.Settings;
using SData.Store;
using SData.Store.Models;

namespace SData.Alerts;

public class AlertHandlers(DatabaseContext db, IIdProvider ids, ISystemSettingsProvider settings, AlertExecutionGate gate, IAnalyticsExecutor executor, TimeProvider clock) :
    IRequestHandler<SaveAlertCommand, AlertItem>,
    IRequestHandler<QueryAlertsCommand, List<AlertItem>>,
    IRequestHandler<DeleteAlertCommand, bool>,
    IRequestHandler<QueryAlertHistoryCommand, AlertHistory>,
    IRequestHandler<QueryAlertChannelsCommand, List<AlertChannelItem>>,
    IRequestHandler<PreviewAlertCommand, AlertPoint>
{
    public async Task<AlertItem> Handle(SaveAlertCommand command, CancellationToken token)
    {
        AlertConditionCompiler.Compile(command.Domain, command.Condition, command.Parameters);
        JsonDocument definition;
        try { definition = JsonDocument.Parse(command.Definition); }
        catch (JsonException) { throw BusinessException.Throw(400, "搜索快照格式不合法"); }
        using var definitionLease = definition;
        if (definition.RootElement.ValueKind != JsonValueKind.Object ||
            !definition.RootElement.TryGetProperty("domain", out var domain) || domain.GetString() != command.Domain)
        {
            throw BusinessException.Throw(400, "搜索快照与告警数据域不一致");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var id = command.Id == null ? 0 : long.Parse(command.Id);
        var rule = id == 0 ? null : await db.AlertRules.SingleOrDefaultAsync(x => x.Id == id, token);
        if (id != 0 && rule == null) throw BusinessException.Throw(404, "告警不存在");
        if (rule != null && rule.Version != command.Version) throw BusinessException.Throw(409, "告警已被修改，请刷新后重试");
        if (command.Enabled && (rule == null || !rule.Enabled) &&
            await db.AlertRules.CountAsync(x => x.Enabled, token) >= Math.Clamp(settings.GetRuntime().Alerts.MaxEnabledRules, 1, 500))
        {
            throw BusinessException.Throw(400, "启用告警数量已达上限");
        }

        var channels = command.Channels.Select(long.Parse).ToArray();
        if (command.Enabled && await db.NotificationChannels.CountAsync(x => channels.Contains(x.Id) && x.Enabled, token) != channels.Length)
        {
            throw BusinessException.Throw(400, "通知渠道不存在或已停用");
        }

        var savedId = command.SavedQueryId == null ? 0 : long.Parse(command.SavedQueryId);
        if (savedId != 0 && !await db.SavedQueries.AnyAsync(x => x.Id == savedId && x.Kind == "search", token))
        {
            throw BusinessException.Throw(404, "保存的搜索不存在");
        }

        if (savedId == 0)
        {
            savedId = ids.GetLongId();
            db.SavedQueries.Add(new SavedQueryEntity { Id = savedId, Name = command.Name.Trim(), Kind = "search", Definition = command.Definition });
        }

        if (rule == null)
        {
            rule = new AlertRuleEntity { Id = ids.GetLongId() };
            db.AlertRules.Add(rule);
        }
        else
        {
            rule.Version++;
        }

        rule.SavedQueryId = savedId;
        rule.Name = command.Name.Trim();
        rule.Domain = command.Domain;
        rule.Definition = command.Definition;
        rule.Condition = command.Condition;
        rule.Parameters = JsonSerializer.Serialize(command.Parameters);
        rule.Channels = JsonSerializer.Serialize(command.Channels);
        rule.Note = command.Note;
        rule.Operator = command.Operator;
        rule.Threshold = command.Threshold;
        rule.WindowMinutes = command.WindowMinutes;
        rule.DelayMinutes = command.DelayMinutes;
        rule.ConsecutiveWindows = command.ConsecutiveWindows;
        rule.Enabled = command.Enabled;
        rule.Status = command.Enabled ? "pending" : "disabled";
        rule.Firing = false;
        rule.Hits = 0;
        rule.Failures = 0;
        rule.LastWindow = AlertState.WindowEnd(clock.GetUtcNow(), rule.WindowMinutes, rule.DelayMinutes);
        rule.NextAttempt = 0;
        rule.Error = string.Empty;
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { throw BusinessException.Throw(409, "告警已被修改，请刷新后重试"); }
        await transaction.CommitAsync(token);
        return ToItem(rule);
    }

    public async Task<List<AlertItem>> Handle(QueryAlertsCommand command, CancellationToken token)
    {
        var query = db.AlertRules.AsNoTracking();
        if (command.SavedQueryId != null)
        {
            var savedId = long.Parse(command.SavedQueryId);
            query = query.Where(x => x.SavedQueryId == savedId);
        }

        return (await query.OrderByDescending(x => x.Id).Take(500).ToListAsync(token)).Select(ToItem).ToList();
    }

    public async Task<bool> Handle(DeleteAlertCommand command, CancellationToken token)
    {
        var id = long.Parse(command.Id);
        var rule = await db.AlertRules.SingleOrDefaultAsync(x => x.Id == id, token) ?? throw BusinessException.Throw(404, "告警不存在");
        if (rule.Version != command.Version) throw BusinessException.Throw(409, "告警已被修改，请刷新后重试");
        db.AlertRules.Remove(rule);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { throw BusinessException.Throw(409, "告警已被修改，请刷新后重试"); }
        return true;
    }

    public async Task<AlertHistory> Handle(QueryAlertHistoryCommand command, CancellationToken token)
    {
        var id = long.Parse(command.Id);
        if (!await db.AlertRules.AnyAsync(x => x.Id == id, token)) throw BusinessException.Throw(404, "告警不存在");
        var points = await db.AlertEvaluations.AsNoTracking().Where(x => x.RuleId == id).OrderByDescending(x => x.WindowEnd).Take(48).ToListAsync(token);
        var deliveries = await db.AlertDeliveries.AsNoTracking().Where(x => x.RuleId == id).OrderByDescending(x => x.Id).Take(20).ToListAsync(token);
        return new AlertHistory(points.OrderBy(x => x.WindowEnd).Select(x => new AlertPoint(DateTimeOffset.FromUnixTimeSeconds(x.WindowEnd), x.Count, x.LowerBound, x.Status, x.Error)).ToList(),
            deliveries.Select(x => new AlertDeliveryItem(x.ChannelId.ToString(), x.Status, x.Attempts, x.Result)).ToList());
    }

    public Task<List<AlertChannelItem>> Handle(QueryAlertChannelsCommand command, CancellationToken token)
        => db.NotificationChannels.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new AlertChannelItem(x.Id.ToString(), x.Name, x.Type, x.Enabled)).ToListAsync(token);

    public async Task<AlertPoint> Handle(PreviewAlertCommand command, CancellationToken token)
    {
        var end = clock.GetUtcNow().AddMinutes(-1);
        var count = await gate.ExecuteAsync(executor, new AlertCountQuery(command.Domain, command.Condition, command.Parameters, end.AddMinutes(-command.WindowMinutes), end, 10001), token);
        return new AlertPoint(end, count.Count, count.LowerBound, "preview", string.Empty);
    }

    internal static AlertItem ToItem(AlertRuleEntity rule) => new()
    {
        Id = rule.Id.ToString(), Version = rule.Version, SavedQueryId = rule.SavedQueryId.ToString(),
        Name = rule.Name, Domain = rule.Domain, Definition = rule.Definition, Condition = rule.Condition,
        Parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(rule.Parameters) ?? new(),
        Channels = JsonSerializer.Deserialize<List<string>>(rule.Channels) ?? new(),
        Note = rule.Note, Operator = rule.Operator, Threshold = rule.Threshold,
        WindowMinutes = rule.WindowMinutes, DelayMinutes = rule.DelayMinutes, ConsecutiveWindows = rule.ConsecutiveWindows,
        Enabled = rule.Enabled, Status = rule.Status, Error = rule.Error,
        CreateTime = rule.CreateTime, UpdateTime = rule.UpdateTime,
        CreateUserId = rule.CreateUserId.ToString(), UpdateUserId = rule.UpdateUserId.ToString(),
    };
}
