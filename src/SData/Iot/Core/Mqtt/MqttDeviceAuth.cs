using System.Security.Cryptography;

namespace SData.Iot.Mqtt;

/// <summary>
/// MQTT 设备凭证校验结果.
/// </summary>
/// <param name="Success">是否通过校验.</param>
/// <param name="DeviceKey">通过校验的设备标识（失败为 null）.</param>
/// <param name="Reason">失败原因（人类可读，用于日志）.</param>
public sealed record MqttCredentialResult(bool Success, string? DeviceKey, string? Reason)
{
    /// <summary>
    /// 校验通过结果.
    /// </summary>
    /// <param name="deviceKey">设备标识.</param>
    /// <returns>结果.</returns>
    public static MqttCredentialResult Ok(string deviceKey) => new(true, deviceKey, null);

    /// <summary>
    /// 校验失败结果.
    /// </summary>
    /// <param name="deviceKey">尝试的设备标识.</param>
    /// <param name="reason">失败原因.</param>
    /// <returns>结果.</returns>
    public static MqttCredentialResult Fail(string? deviceKey, string reason) => new(false, null, reason);
}

/// <summary>
/// 设备接入校验器：username = DeviceKey，password 按免 key、统一 key 或前缀策略校验.
/// 实现须拒绝禁用设备；未知或已删除设备通过认证后首次有效发布注册新台账；无实现注册时拒绝全部连接.
/// </summary>
public interface IDeviceCredentialChecker
{
    /// <summary>
    /// 校验设备凭证.
    /// </summary>
    /// <param name="deviceKey">设备标识（MQTT username）.</param>
    /// <param name="secret">MQTT password 中的接入 key.</param>
    /// <param name="cancellationToken">取消令牌.</param>
    /// <returns>校验结果.</returns>
    Task<MqttCredentialResult> CheckAsync(string deviceKey, string secret, CancellationToken cancellationToken);
}

/// <summary>
/// 设备密钥哈希工具（T140/T141 共用存储格式：pbkdf2-sha256$&lt;iterations&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;）.
/// </summary>
public static class MqttPasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// <summary>
    /// 生成设备密钥哈希（PBKDF2-SHA256，随机盐）.
    /// </summary>
    /// <param name="secret">明文密钥.</param>
    /// <returns>可存储的哈希串.</returns>
    public static string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// 校验明文密钥与存储哈希是否匹配（固定时间比较，防时序侧信道）.
    /// </summary>
    /// <param name="secret">明文密钥.</param>
    /// <param name="storedHash">存储哈希.</param>
    /// <returns>是否匹配.</returns>
    public static bool Verify(string secret, string storedHash)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out var iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
