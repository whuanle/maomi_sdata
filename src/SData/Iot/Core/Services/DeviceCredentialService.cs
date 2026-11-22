using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SData.Database;
using SData.Infra.Services;
using SData.Iot.Mqtt;
using SData.Settings;

namespace SData.Iot.Services;

/// <summary>
/// 设备接入服务：设备状态与可配置 key 校验，允许合法未知设备首次发布时注册，保留旧哈希工具与 LastSeen 内存累积.
/// 同时实现 <see cref="IDeviceCredentialChecker"/>，供 MQTT broker 校验 CONNECT 凭证（T140↔T141 衔接）.
/// 本服务为单例（broker 托管服务依赖），Scoped 的 <see cref="DatabaseContext"/> 经 IServiceScopeFactory 每次校验取独立作用域（同 DeviceLastSeenFlusher 模式）.
/// </summary>
public class DeviceCredentialService : IDeviceCredentialChecker
{
    private readonly IPasswordHasher _passwordHasher;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DeviceLastSeenBuffer _lastSeenBuffer;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceCredentialService"/> class.
    /// </summary>
    /// <param name="passwordHasher">密码哈希器（PBKDF2）.</param>
    /// <param name="scopeFactory">服务作用域工厂（每次校验取独立 DatabaseContext）.</param>
    /// <param name="lastSeenBuffer">LastSeen 内存累积器.</param>
    public DeviceCredentialService(IPasswordHasher passwordHasher, IServiceScopeFactory scopeFactory, DeviceLastSeenBuffer lastSeenBuffer)
    {
        _passwordHasher = passwordHasher;
        _scopeFactory = scopeFactory;
        _lastSeenBuffer = lastSeenBuffer;
    }

    /// <summary>
    /// 生成设备密钥哈希（创建/重置密钥时使用）.
    /// </summary>
    /// <param name="secret">明文密钥.</param>
    /// <returns>哈希串.</returns>
    public string HashSecret(string secret)
    {
        return _passwordHasher.Hash(secret);
    }

    /// <summary>
    /// 校验设备标识、状态与接入策略（未知设备允许接入，不在 CONNECT 时创建）.
    /// </summary>
    /// <param name="deviceKey">设备标识.</param>
    /// <param name="secret">MQTT password，作为统一 key 或前缀匹配输入.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>false = 标识非法、禁用或 key 不匹配；删除视为新设备.</returns>
    public async Task<bool> ValidateAsync(string deviceKey, string secret, CancellationToken cancellationToken = default)
    {
        if (!DeviceKeyRules.IsValid(deviceKey))
        {
            return false;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var databaseContext = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var device = await databaseContext.Devices.AsNoTracking()
            .FirstOrDefaultAsync(x => x.DeviceKey == deviceKey, cancellationToken);
        if (device != null && !device.Enabled)
        {
            return false;
        }

        var settings = scope.ServiceProvider.GetRequiredService<ISystemSettingsProvider>();
        var auth = settings.GetMqttAuth();
        if (!auth.IsValid) return false;
        if (auth.Mode == "none") return true;
        if (string.IsNullOrEmpty(secret) || secret.Length > 1024) return false;
        if (auth.Mode == "prefix" && secret.Length < auth.PrefixLength) return false;
        try
        {
            return _passwordHasher.Verify(auth.Mode == "prefix" ? secret[..auth.PrefixLength] : secret, auth.KeyHash);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<MqttCredentialResult> CheckAsync(string deviceKey, string secret, CancellationToken cancellationToken)
    {
        if (await ValidateAsync(deviceKey, secret, cancellationToken))
        {
            return MqttCredentialResult.Ok(deviceKey);
        }

        return MqttCredentialResult.Fail(deviceKey, "设备不可用或接入 key 不匹配");
    }

    /// <summary>
    /// 记录设备活跃（内存累积，由 DeviceLastSeenFlusher 批量回写）.
    /// O(1) 内存累积，禁止逐消息写 SQLite.
    /// </summary>
    /// <param name="deviceKey">设备标识.</param>
    /// <param name="time">上报时间.</param>
    public void Touch(string deviceKey, DateTimeOffset time)
    {
        _lastSeenBuffer.Touch(deviceKey, time);
    }
}
