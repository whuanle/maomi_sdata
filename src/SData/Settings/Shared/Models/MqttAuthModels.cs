using MediatR;
using SData.Infra.Models;
using SData.Infra.Services;

namespace SData.Settings;

/// <summary>MQTT 认证覆盖配置；不保存明文 key.</summary>
public class MqttAuthOptions
{
    public string Mode { get; set; } = "none";
    public string KeyHash { get; set; } = string.Empty;
    public int PrefixLength { get; set; }
    public bool IsValid => Mode == "none" || (Mode is "shared" or "prefix"
        && !string.IsNullOrEmpty(KeyHash) && PrefixLength is >= 1 and <= 256);
}

/// <summary>管理员可见的非敏感认证设置.</summary>
public class MqttAuthSettingsResponse
{
    public string Mode { get; set; } = "none";
    public bool HasKey { get; set; }
}

/// <summary>查询 MQTT 接入认证.</summary>
public class QueryMqttAuthSettingsCommand : IRequest<MqttAuthSettingsResponse>, IModelValidator<QueryMqttAuthSettingsCommand>
{
    public static void Validate(AbstractValidator<QueryMqttAuthSettingsCommand> validate) { }
}

/// <summary>空 key 仅在同模式下保留已配置值.</summary>
public class SaveMqttAuthSettingsCommand : IRequest<MqttAuthSettingsResponse>, IUserIdContext, IModelValidator<SaveMqttAuthSettingsCommand>
{
    public string Mode { get; set; } = "none";
    public string Key { get; set; } = string.Empty;
    public long ContextUserId { get; set; }
    public string ContextUserRole { get; set; } = string.Empty;

    public static void Validate(AbstractValidator<SaveMqttAuthSettingsCommand> validate)
    {
        validate.RuleFor(x => x.Mode).Must(x => x is "none" or "shared" or "prefix");
        validate.RuleFor(x => x.Key).NotNull().MaximumLength(256);
    }
}
