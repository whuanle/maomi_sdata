namespace SData.Store.Models;

/// <summary>共享写入器最近失败，原因已分类，不含载荷或凭据.</summary>
public record IngestWriterFailure(string Code, string Reason, DateTimeOffset At, DateTimeOffset? RecoveredAt);


/// <summary>
/// 接入缓冲统计.
/// </summary>
public class IngestBufferStats
{
    /// <summary>共享批量写入器的安全失败快照；不含原始异常.</summary>
    public IngestWriterFailure? WriterFailure { get; set; }

    /// <summary>
    /// 各类型缓冲中条数：logs/traces/metrics/iot.
    /// </summary>
    public Dictionary<string, long> Buffered { get; set; } = new();

    /// <summary>
    /// 已落库条数.
    /// </summary>
    public Dictionary<string, long> Processed { get; set; } = new();

    /// <summary>
    /// 因缓冲满丢弃条数.
    /// </summary>
    public Dictionary<string, long> Dropped { get; set; } = new();

    /// <summary>缓冲中已估算的载荷字节数.</summary>
    public long BufferedBytes { get; set; }

    /// <summary>接入字节预算上限（启动配置）.</summary>
    public long ByteBudgetBytes { get; set; }
}
