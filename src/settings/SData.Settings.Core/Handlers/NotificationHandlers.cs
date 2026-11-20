using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Exceptions;
using SData.Infra.Services;
using SData.Settings.Services;

namespace SData.Settings.Handlers;

/// <summary>通知配置与发送共用的应用服务.</summary>
public class NotificationService(DatabaseContext database, IIdProvider ids, NotificationConfiguration configuration, NotificationTransport transport, ILogger<NotificationService> logger) : INotificationSender
{
    public async Task<List<NotificationChannelItem>> QueryAsync(CancellationToken token)
    {
        var entities = await database.NotificationChannels.AsNoTracking().ToListAsync(token);
        return entities.Select(ToItem).OrderBy(x => x.Name).ToList();
    }

    public async Task<NotificationChannelItem> SaveAsync(SaveNotificationChannelCommand command, CancellationToken token)
    {
        var entity = command.Id == null ? new NotificationChannelEntity { Id = ids.GetLongId() } : await GetAsync(command.Id, command.Version, token);
        if (command.Id != null && entity.Type != command.Type) throw BusinessException.Throw(400, "不能修改通知渠道类型");
        var config = configuration.Merge(command, command.Id == null ? null : configuration.Unprotect(entity.ProtectedConfig));
        using (var request = NotificationProtocols.Build(command.Type, config, new NotificationMessage()))
        {
            transport.Validate(command.Type, request.RequestUri!);
        }

        var name = command.Name.Trim();
        if (await database.NotificationChannels.AnyAsync(x => x.Type == command.Type && x.Name == name && x.Id != entity.Id, token))
        {
            throw BusinessException.Throw(400, "同类型通知渠道的实例名称不能重复");
        }

        entity.Name = name;
        entity.Type = command.Type;
        entity.Enabled = command.Enabled;
        entity.ProtectedConfig = configuration.Protect(config);
        if (command.Id == null) database.NotificationChannels.Add(entity);
        else entity.Version++;
        await CommitAsync(token);
        logger.LogInformation("已保存通知渠道 {ChannelId} {ChannelType}", entity.Id, entity.Type);
        return ToItem(entity);
    }

    public async Task<bool> DeleteAsync(DeleteNotificationChannelCommand command, CancellationToken token)
    {
        var entity = await GetAsync(command.Id, command.Version, token);
        database.NotificationChannels.Remove(entity);
        entity.Version++;
        await CommitAsync(token);
        logger.LogInformation("已删除通知渠道 {ChannelId}", entity.Id);
        return true;
    }

    public async Task<NotificationSendResult> TestAsync(TestNotificationChannelCommand command, CancellationToken token)
    {
        var entity = command.Id == null ? null : await GetAsync(command.Id, command.Version, token);
        if (entity != null && entity.Type != command.Type) throw BusinessException.Throw(400, "不能修改通知渠道类型");
        var config = configuration.Merge(command, entity == null ? null : configuration.Unprotect(entity.ProtectedConfig));
        return await DeliverAsync(command.Type, config, new NotificationMessage { Title = "SData 测试通知", Body = "通知渠道测试消息。收到此消息表示发送配置可用。" }, token);
    }

    public async Task<NotificationSendResult> SendAsync(long channelId, NotificationMessage message, CancellationToken cancellationToken)
    {
        var entity = await database.NotificationChannels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == channelId, cancellationToken);
        if (entity == null || !entity.Enabled) return new(false, "通知渠道不存在或已停用");
        if (string.IsNullOrWhiteSpace(message.Title) || message.Title.Length > 256 || message.Body == null || message.Body.Length > 16000)
            return new(false, "通知标题或正文无效");
        return await DeliverAsync(entity.Type, configuration.Unprotect(entity.ProtectedConfig), message, cancellationToken);
    }

    private async Task<NotificationSendResult> DeliverAsync(string type, Dictionary<string, string> config, NotificationMessage message, CancellationToken token)
    {
        try
        {
            using var request = NotificationProtocols.Build(type, config, message);
            var response = await transport.SendAsync(type, request, token);
            var success = NotificationProtocols.IsSuccess(type, (int)response.Status, response.Body);
            logger.LogInformation("通知发送完成 {ChannelType} {Success}", type, success);
            return new(success, success ? "通知已发送" : "通知服务拒绝请求，请检查渠道配置与服务配额");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            // 不记录异常对象：URI、响应和异常消息可能包含凭据.
            logger.LogWarning("通知发送失败 {ChannelType}", type);
            return new(false, "通知发送失败，请检查地址、凭据与网络连接");
        }
    }

    private async Task<NotificationChannelEntity> GetAsync(string id, long version, CancellationToken token)
    {
        if (!long.TryParse(id, out var value)) throw BusinessException.Throw(400, "通知渠道编号无效");
        var entity = await database.NotificationChannels.SingleOrDefaultAsync(x => x.Id == value, token)
            ?? throw BusinessException.Throw(404, "通知渠道不存在");
        if (entity.Version != version) throw BusinessException.Throw(409, "通知渠道已更新，请刷新后重试");
        return entity;
    }

    private async Task CommitAsync(CancellationToken token)
    {
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { throw BusinessException.Throw(409, "通知渠道已更新，请刷新后重试"); }
    }

    private NotificationChannelItem ToItem(NotificationChannelEntity entity)
    {
        var config = configuration.Unprotect(entity.ProtectedConfig);
        return new NotificationChannelItem
        {
            Id = entity.Id.ToString(), Name = entity.Name, Type = entity.Type, Enabled = entity.Enabled, Version = entity.Version,
            Config = config.Where(x => !NotificationTypes.IsSecret(x.Key)).ToDictionary(),
            ConfiguredSecrets = config.Where(x => NotificationTypes.IsSecret(x.Key) && !string.IsNullOrEmpty(x.Value)).Select(x => x.Key).ToList(),
            CreateTime = entity.CreateTime, UpdateTime = entity.UpdateTime,
            CreateUserId = entity.CreateUserId.ToString(), UpdateUserId = entity.UpdateUserId.ToString(),
        };
    }
}

public class QueryNotificationChannelsHandler(NotificationService service) : IRequestHandler<QueryNotificationChannelsCommand, List<NotificationChannelItem>>
{
    public Task<List<NotificationChannelItem>> Handle(QueryNotificationChannelsCommand request, CancellationToken cancellationToken) => service.QueryAsync(cancellationToken);
}

public class SaveNotificationChannelHandler(NotificationService service) : IRequestHandler<SaveNotificationChannelCommand, NotificationChannelItem>
{
    public Task<NotificationChannelItem> Handle(SaveNotificationChannelCommand request, CancellationToken cancellationToken) => service.SaveAsync(request, cancellationToken);
}

public class DeleteNotificationChannelHandler(NotificationService service) : IRequestHandler<DeleteNotificationChannelCommand, bool>
{
    public Task<bool> Handle(DeleteNotificationChannelCommand request, CancellationToken cancellationToken) => service.DeleteAsync(request, cancellationToken);
}

public class TestNotificationChannelHandler(NotificationService service) : IRequestHandler<TestNotificationChannelCommand, NotificationSendResult>
{
    public Task<NotificationSendResult> Handle(TestNotificationChannelCommand request, CancellationToken cancellationToken) => service.TestAsync(request, cancellationToken);
}
