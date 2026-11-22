namespace SData.Database.Entities;

/// <summary>
/// 数据接入 API Key.
/// </summary>
public class IngestKeyEntity : Audits.FullAuditedEntity
{
    /// <summary>
    /// id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 名称.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// key 的 SHA256（不存明文）.
    /// </summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>仅用于识别凭据的非秘密前缀.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>管理员查看使用的受保护秘密；旧凭据为空.</summary>
    public string ProtectedSecret { get; set; } = string.Empty;
}
