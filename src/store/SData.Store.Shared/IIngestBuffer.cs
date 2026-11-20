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

    /// <summary>
    /// 写入指标点.
    /// </summary>
    /// <param name="record">指标点.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    bool TryEnqueueMetric(MetricPointRecord record);

    /// <summary>
    /// 写入遥测.
    /// </summary>
    /// <param name="record">遥测记录.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    bool TryEnqueueTelemetry(TelemetryRecord record);

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
}
