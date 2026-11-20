namespace SData.Database.Entities;

/// <summary>
/// 物联网设备.
/// </summary>
public class DeviceEntity : Audits.FullAuditedEntity
{
    /// <summary>
    /// id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 设备标识（唯一，接入时使用）.
    /// </summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>
    /// 设备名称.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 分组（产线/车间等）.
    /// </summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// 标签（JSON 对象）.
    /// </summary>
    public string TagsJson { get; set; } = "{}";

    /// <summary>
    /// 是否启用.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 设备密钥哈希（PBKDF2，格式 pbkdf2$iterations$salt$hash；空 = 未设置，接入校验直接拒绝）.
    /// </summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>
    /// 最近上报时间.
    /// </summary>
    public DateTimeOffset? LastSeenTime { get; set; }
}
