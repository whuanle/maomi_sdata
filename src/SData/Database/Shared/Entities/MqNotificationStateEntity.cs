namespace SData.Database.Entities;

/// <summary>队列/渠道告警冷却元数据，消费者重建不清除.</summary>
public class MqNotificationStateEntity : Audits.FullAuditedEntity
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public string Topic { get; set; } = string.Empty;
    public long ChannelId { get; set; }
    public DateTimeOffset SuppressedUntil { get; set; }
    public long SuppressedCount { get; set; }
    public string LastFailureId { get; set; } = string.Empty;
    public string DeliveryStatus { get; set; } = string.Empty;
    public DateTimeOffset? LastAttemptAt { get; set; }
}
