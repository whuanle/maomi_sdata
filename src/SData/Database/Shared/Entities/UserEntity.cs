namespace SData.Database.Entities;

/// <summary>
/// 用户.
/// </summary>
public class UserEntity : Audits.FullAuditedEntity
{
    /// <summary>
    /// id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 用户名（唯一）.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 密码哈希（PBKDF2）.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// 角色：admin（超级管理员）/ write（可写）/ readonly（只读）.
    /// </summary>
    public string Role { get; set; } = SData.Infra.SDataRoles.ReadOnly;
}
