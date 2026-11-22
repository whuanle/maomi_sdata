namespace SData.Store.Models;

/// <summary>消费会话的累计采样；不可跨采样求和，重试覆盖使用相同会话标识.</summary>
public class MqConsumptionRecord
{
    public DateTimeOffset Timestamp { get; set; }
    public long InstanceId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Rule { get; set; } = string.Empty;
    public string MatchMode { get; set; } = "exact";
    public string State { get; set; } = "starting";
    public long ReceivedMessages { get; set; }
    public long ReceivedBytes { get; set; }
    public long PersistedRecords { get; set; }
    public long ParseFailures { get; set; }
    public long SkippedMessages { get; set; }
    public long ConfirmedMessages { get; set; }
    public long ConfirmationFailures { get; set; }
    public long SamplingGaps { get; set; }
    public string? DiagnosticsJson { get; set; }
}

/// <summary>按时间范围读取消费采样.</summary>
public interface IMqConsumptionStore
{
    Task<IReadOnlyList<MqConsumptionRecord>> QueryAsync(DateTimeOffset from, DateTimeOffset to, long? instanceId, CancellationToken cancellationToken);
}
