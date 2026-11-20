namespace SData.Database.Entities;

/// <summary>
/// 外部通知渠道（配置加密保存，供告警通过稳定 id 引用）.
/// </summary>
public class NotificationChannelEntity : Audits.FullAuditedEntity
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public string ProtectedConfig { get; set; } = string.Empty;

    public long Version { get; set; } = 1;
}
