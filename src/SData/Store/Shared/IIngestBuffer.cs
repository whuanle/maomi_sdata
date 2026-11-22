using SData.Store.Models;

namespace SData.Store;

/// <summary>
/// 接入内存缓冲（批量落库前置队列）.
/// </summary>
public interface IIngestBuffer
{
    /// <summary>
    /// 写入日志.
    /// </summary>
    /// <param name="record">日志记录.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    bool TryEnqueueLog(LogRecord record);

    /// <summary>预检整批字节是否可容纳；估算超预算时调用方应在解析后/入队前返回 429.</summary>
    bool CanAcceptBytes(long bytes) => true;

    /// <summary>缓冲当前条数（近似值，供背压快速拒绝使用）.</summary>
    int ApproximateBufferedCount => 0;

    /// <summary>缓冲条数容量（超过即背压拒绝）.</summary>
    int Capacity => 0;

    /// <summary>批量写入日志；失败条数仍按单条缓冲满载计数.</summary>
    (long Accepted, long Dropped, IReadOnlyList<LogRecord> Rejected) TryEnqueueLogs(IReadOnlyList<LogRecord> records)
        => EnqueueBatch(records, TryEnqueueLog);

    /// <summary>异步写入日志并等待批量落库完成；取消时调用方不得确认 MQ 消息.</summary>
    Task PersistLogsAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken)
        => throw new NotSupportedException("接入缓冲未实现持久化确认");

    /// <summary>非阻塞写入消费采样；满时由采样器合并后重试.</summary>
    bool TryEnqueueMqConsumption(MqConsumptionRecord record) => false;

    /// <summary>
    /// 写入 span.
    /// </summary>
    /// <param name="record">span 记录.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    bool TryEnqueueSpan(SpanRecord record);

    /// <summary>批量写入 span；失败条数仍按单条缓冲满载计数.</summary>
    (long Accepted, long Dropped, IReadOnlyList<SpanRecord> Rejected) TryEnqueueSpans(IReadOnlyList<SpanRecord> records)
        => EnqueueBatch(records, TryEnqueueSpan);

    /// <summary>
    /// 写入指标点.
    /// </summary>
    /// <param name="record">指标点.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    bool TryEnqueueMetric(MetricPointRecord record);

    /// <summary>批量写入指标；失败条数仍按单条缓冲满载计数.</summary>
    (long Accepted, long Dropped, IReadOnlyList<MetricPointRecord> Rejected) TryEnqueueMetrics(IReadOnlyList<MetricPointRecord> records)
        => EnqueueBatch(records, TryEnqueueMetric);

    /// <summary>
    /// 写入遥测.
    /// </summary>
    /// <param name="record">遥测记录.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    bool TryEnqueueTelemetry(TelemetryRecord record);

    /// <summary>批量写入遥测；失败条数仍按单条缓冲满载计数.</summary>
    (long Accepted, long Dropped, IReadOnlyList<TelemetryRecord> Rejected) TryEnqueueTelemetry(IReadOnlyList<TelemetryRecord> records)
        => EnqueueBatch(records, TryEnqueueTelemetry);

    /// <summary>
    /// 写入 RUM 行为事件（T610 契约增量）.
    /// </summary>
    /// <param name="record">RUM 事件记录.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    bool TryEnqueueRumEvent(RumEventRecord record);

    /// <summary>
    /// 写入 RUM 会话记录.
    /// </summary>
    /// <param name="record">RUM 会话记录.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    bool TryEnqueueRumSession(RumSessionRecord record);

    /// <summary>
    /// 写入 RUM 回放分片.
    /// </summary>
    /// <param name="record">RUM 回放分片.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    bool TryEnqueueRumReplay(RumReplayRecord record);

    /// <summary>
    /// 获取缓冲统计.
    /// </summary>
    /// <returns>统计.</returns>
    IngestBufferStats GetStats();

    private static (long Accepted, long Dropped, IReadOnlyList<T> Rejected) EnqueueBatch<T>(IReadOnlyList<T> records, Func<T, bool> enqueue)
    {
        long accepted = 0;
        long dropped = 0;
        var rejected = new List<T>();
        for (var i = 0; i < records.Count; i++)
        {
            if (enqueue(records[i]))
            {
                accepted++;
            }
            else
            {
                dropped++;
                rejected.Add(records[i]);
            }
        }

        return (accepted, dropped, rejected);
    }
}
