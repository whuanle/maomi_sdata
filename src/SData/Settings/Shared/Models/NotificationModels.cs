using Mediator;
using SData.Infra.Services;
using SData.Infra.Models;

namespace SData.Settings;

/// <summary>通知类型与字段白名单；秘密字段只接受替换值或保留指令.</summary>
public static class NotificationTypes
{
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>
    {
        ["telegram"] = ["token", "chatId"],
        ["discord"] = ["webhook"],
        ["feishu"] = ["webhook", "secret"],
        ["dingtalk"] = ["webhook", "secret"],
        ["wecom"] = ["webhook"],
        ["bark"] = ["server", "token"],
        ["gotify"] = ["server", "token"],
        ["serverchan"] = ["token"],
        ["pushdeer"] = ["server", "token"],
        ["matrix"] = ["server", "token", "roomId"],
        ["webhook"] = ["webhook", "headers", "template"],
    };

    public static bool IsSecret(string field) => field is "token" or "secret" or "webhook" or "headers";
}

public class NotificationChannelItem
{
    // 字符串防止 JavaScript 对雪花 id 丢失精度.
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public long Version { get; set; }
    public Dictionary<string, string> Config { get; set; } = new();
    public List<string> ConfiguredSecrets { get; set; } = new();
    public DateTimeOffset CreateTime { get; set; }
    public DateTimeOffset UpdateTime { get; set; }
    public string CreateUserId { get; set; } = string.Empty;
    public string UpdateUserId { get; set; } = string.Empty;
}

public class NotificationMessage
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Level { get; set; } = "info";
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
    public string? Link { get; set; }
}

public record NotificationSendResult(bool Success, string Message);

/// <summary>供未来 Alert 调用；不暴露渠道凭据，不负责持久重试.</summary>
public interface INotificationSender
{
    Task<NotificationSendResult> SendAsync(long channelId, NotificationMessage message, CancellationToken cancellationToken);
}

public class QueryNotificationChannelsCommand : IRequest<List<NotificationChannelItem>>, IModelValidator<QueryNotificationChannelsCommand>
{
    public static void Validate(AbstractValidator<QueryNotificationChannelsCommand> validate) { }
}

public class NotificationChannelDraft : IUserIdContext
{
    public string? Id { get; set; }
    public long Version { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public Dictionary<string, string> Config { get; set; } = new();
    public List<string> KeepSecrets { get; set; } = new();
    public long ContextUserId { get; set; }
    public string ContextUserRole { get; set; } = string.Empty;

}

public class SaveNotificationChannelCommand : NotificationChannelDraft, IRequest<NotificationChannelItem>, IModelValidator<SaveNotificationChannelCommand>
{
    public static void Validate(AbstractValidator<SaveNotificationChannelCommand> validate)
    {
        validate.RuleFor(x => x.Id).Must(x => x == null || (long.TryParse(x, out var id) && id > 0));
        validate.RuleFor(x => x.Version).GreaterThanOrEqualTo(0);
        validate.RuleFor(x => x.Name).NotEmpty().MaximumLength(64);
        validate.RuleFor(x => x.Type).Must(x => x != null && NotificationTypes.Fields.ContainsKey(x));
        validate.RuleFor(x => x.Config).NotNull().Must(x => x != null && x.Count <= 8 && x.All(p => p.Value != null && p.Value.Length <= 16000));
        validate.RuleFor(x => x.KeepSecrets).NotNull().Must(x => x != null && x.Count <= 8);
    }
}

public class DeleteNotificationChannelCommand : IRequest<bool>, IUserIdContext, IModelValidator<DeleteNotificationChannelCommand>
{
    public string Id { get; set; } = string.Empty;
    public long Version { get; set; }
    public long ContextUserId { get; set; }
    public string ContextUserRole { get; set; } = string.Empty;

    public static void Validate(AbstractValidator<DeleteNotificationChannelCommand> validate)
    {
        validate.RuleFor(x => x.Id).Must(x => long.TryParse(x, out var id) && id > 0);
        validate.RuleFor(x => x.Version).GreaterThan(0);
    }
}

public class TestNotificationChannelCommand : NotificationChannelDraft, IRequest<NotificationSendResult>, IModelValidator<TestNotificationChannelCommand>
{
    public static void Validate(AbstractValidator<TestNotificationChannelCommand> validate)
    {
        validate.RuleFor(x => x.Name).NotEmpty().MaximumLength(64);
        validate.RuleFor(x => x.Id).Must(x => x == null || (long.TryParse(x, out var id) && id > 0));
        validate.RuleFor(x => x.Type).Must(x => x != null && NotificationTypes.Fields.ContainsKey(x));
        validate.RuleFor(x => x.Config).NotNull().Must(x => x != null && x.Count <= 8 && x.All(p => p.Value != null && p.Value.Length <= 16000));
        validate.RuleFor(x => x.KeepSecrets).NotNull().Must(x => x != null && x.Count <= 8);
    }
}
