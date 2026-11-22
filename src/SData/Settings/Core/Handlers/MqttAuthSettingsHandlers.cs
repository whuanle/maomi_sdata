using System.Text.Json;
using Mediator;
using SData.Infra.Exceptions;
using SData.Infra.Services;
using SData.Settings.Services;

namespace SData.Settings.Handlers;

/// <summary>只返回认证模式与已配置标记.</summary>
public class QueryMqttAuthSettingsCommandHandler(ISystemSettingsProvider settings) : IRequestHandler<QueryMqttAuthSettingsCommand, MqttAuthSettingsResponse>
{
    public ValueTask<MqttAuthSettingsResponse> Handle(QueryMqttAuthSettingsCommand request, CancellationToken cancellationToken)
    {
        var value = settings.GetMqttAuth();
        return ValueTask.FromResult(new MqttAuthSettingsResponse { Mode = value.IsValid ? value.Mode : "invalid", HasKey = value.IsValid && value.Mode != "none" });
    }
}

/// <summary>认证材料只存哈希；更新后供新 MQTT 连接读取.</summary>
public class SaveMqttAuthSettingsCommandHandler(ISystemSettingsProvider settings, IPasswordHasher hasher) : IRequestHandler<SaveMqttAuthSettingsCommand, MqttAuthSettingsResponse>
{
    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    public async ValueTask<MqttAuthSettingsResponse> Handle(SaveMqttAuthSettingsCommand request, CancellationToken cancellationToken)
    {
        if (request.Mode is not ("none" or "shared" or "prefix") || request.Key == null || request.Key.Length > 256)
            throw BusinessException.Throw(400, "MQTT 认证模式或 key 长度无效");
        await SaveLock.WaitAsync(cancellationToken);
        try
        {
            var current = settings.GetMqttAuth();
            var next = new MqttAuthOptions { Mode = request.Mode };
            if (request.Mode != "none")
            {
                if (request.Key.Length == 0)
                {
                    if (!current.IsValid || current.Mode != request.Mode)
                        throw BusinessException.Throw(400, "切换认证模式时必须填写 key 或非空前缀");
                    next.KeyHash = current.KeyHash;
                    next.PrefixLength = current.PrefixLength;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(request.Key))
                        throw BusinessException.Throw(400, "key 或前缀不能仅包含空白");
                    next.KeyHash = hasher.Hash(request.Key);
                    next.PrefixLength = request.Key.Length;
                }
            }

            await settings.SaveAsync(SettingKeys.MqttAuth, JsonSerializer.Serialize(next, SystemSettingsProvider.JsonOptions), cancellationToken);
            return new() { Mode = next.Mode, HasKey = next.Mode != "none" };
        }
        finally { SaveLock.Release(); }
    }
}
