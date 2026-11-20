using System.Threading.Channels;
using DuckDB.NET.Data;
using SData.Infra.Configuration;
using SData.Store.Models;

namespace SData.Store;

/// <summary>
/// 接入数据类型.
/// </summary>
public enum IngestKind
{
    /// <summary>日志.</summary>
    Log,

    /// <summary>链路 span.</summary>
    Trace,

    /// <summary>指标.</summary>
    Metric,

    /// <summary>遥测.</summary>
    Telemetry,

    /// <summary>RUM 行为事件.</summary>
    RumEvent,

    /// <summary>RUM 会话.</summary>
    RumSession,

    /// <summary>RUM 回放分片.</summary>
    RumReplay,

    /// <summary>消费统计采样.</summary>
    MqConsumption,
}

/// <summary>
/// 缓冲条目.
/// </summary>
public readonly struct IngestItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IngestItem"/> struct.
    /// </summary>
    /// <param name="kind">类型.</param>
    /// <param name="record">记录.</param>
    public IngestItem(IngestKind kind, object record, TaskCompletionSource? persisted = null)
    {
        Kind = kind;
        Record = record;
        Persisted = persisted;
    }

    /// <summary>
    /// 类型.
    /// </summary>
    public IngestKind Kind { get; }

    /// <summary>
    /// 记录.
    /// </summary>
    public object Record { get; }

    /// <summary>可选的持久化完成回执.</summary>
    public TaskCompletionSource? Persisted { get; }
}

/// <summary>
/// 接入内存缓冲实现（有界 Channel，满则丢弃并计数）.
/// </summary>
public class IngestBuffer : IIngestBuffer
{
    private readonly Channel<IngestItem> _channel;
    private readonly int _capacity;
    private IngestWriterFailure? _writerFailure;

    public void WriterFailed(Exception exception)
    {
        var (code, reason) = ClassifyFailure(exception);
        Volatile.Write(ref _writerFailure, new IngestWriterFailure(code, reason, DateTimeOffset.UtcNow, null));
    }

    private static (string Code, string Reason) ClassifyFailure(Exception exception)
    {
        var failures = new List<Exception>();
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            failures.Add(current);
        }

        if (failures.OfType<DuckDBException>().Any(error =>
            error.Message.Contains("database has been invalidated", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("FATAL", StringComparison.OrdinalIgnoreCase)))
        {
            return ("database_invalidated", "DuckDB 已失效，无法继续落库；请检查服务日志并重启服务，未提交消息不会确认");
        }

        if (failures.Any(error => error is OutOfMemoryException
            || error is DuckDBException && error.Message.Contains("Out of Memory", StringComparison.OrdinalIgnoreCase)))
        {
            return ("storage_memory", "DuckDB 内存不足，批次保留重试；请降低查询并发和线程数，并检查内存配额及临时磁盘空间");
        }

        if (failures.Any(error => error is IOException
            || error is DuckDBException && error.Message.Contains("IO Error", StringComparison.OrdinalIgnoreCase)))
        {
            return ("storage_io", "存储读写失败，请检查磁盘空间与权限");
        }

        return ("batch_write_failed", "DuckDB 批量提交失败，批次保留重试，请检查服务日志");
    }

    public void WriterRecovered()
    {
        var failure = Volatile.Read(ref _writerFailure);
        if (failure != null && failure.RecoveredAt == null)
            Interlocked.CompareExchange(ref _writerFailure, failure with { RecoveredAt = DateTimeOffset.UtcNow }, failure);
    }
    private readonly long[] _processed = new long[8];
    private readonly long[] _dropped = new long[8];

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestBuffer"/> class.
    /// </summary>
    /// <param name="systemOptions">系统配置.</param>
    public IngestBuffer(SystemOptions systemOptions)
    {
        _capacity = Math.Max(1, systemOptions.Ingest.BufferCapacity);
        _channel = Channel.CreateBounded<IngestItem>(new BoundedChannelOptions(_capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>
    /// 读取通道（写入器使用）.
    /// </summary>
    public ChannelReader<IngestItem> Reader => _channel.Reader;

    /// <summary>
    /// 写入数据.
    /// </summary>
    /// <param name="kind">类型.</param>
    /// <param name="record">记录.</param>
    /// <returns>false = 缓冲满被丢弃.</returns>
    public bool TryEnqueue(IngestKind kind, object record)
    {
        if (_channel.Writer.TryWrite(new IngestItem(kind, record)))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped[(int)kind]);
        return false;
    }

    /// <inheritdoc/>
    public async Task PersistLogsAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken)
    {
        var receipts = new List<Task>(records.Count);
        foreach (var record in records)
        {
            var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _channel.Writer.WriteAsync(new IngestItem(IngestKind.Log, record, receipt), cancellationToken);
            receipts.Add(receipt.Task);
        }

        await Task.WhenAll(receipts).WaitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public bool TryEnqueueLog(LogRecord record) => TryEnqueue(IngestKind.Log, record);

    /// <inheritdoc/>
    public bool TryEnqueueMqConsumption(MqConsumptionRecord record) => TryEnqueue(IngestKind.MqConsumption, record);

    /// <inheritdoc/>
    public bool TryEnqueueSpan(SpanRecord record) => TryEnqueue(IngestKind.Trace, record);

    /// <inheritdoc/>
    public bool TryEnqueueMetric(MetricPointRecord record) => TryEnqueue(IngestKind.Metric, record);

    /// <inheritdoc/>
    public bool TryEnqueueTelemetry(TelemetryRecord record) => TryEnqueue(IngestKind.Telemetry, record);

    /// <inheritdoc/>
    public bool TryEnqueueRumEvent(RumEventRecord record) => TryEnqueue(IngestKind.RumEvent, record);

    /// <inheritdoc/>
    public bool TryEnqueueRumSession(RumSessionRecord record) => TryEnqueue(IngestKind.RumSession, record);

    /// <inheritdoc/>
    public bool TryEnqueueRumReplay(RumReplayRecord record) => TryEnqueue(IngestKind.RumReplay, record);

    /// <inheritdoc/>
    public IngestBufferStats GetStats()
    {
        var stats = new IngestBufferStats { WriterFailure = Volatile.Read(ref _writerFailure) };
        for (var i = 0; i < _processed.Length; i++)
        {
            var name = ((IngestKind)i).ToString().ToLowerInvariant();
            stats.Buffered[name] = _channel.Reader.Count;
            stats.Processed[name] = Interlocked.Read(ref _processed[i]);
            stats.Dropped[name] = Interlocked.Read(ref _dropped[i]);
        }

        return stats;
    }

    /// <summary>
    /// 写入器标记一批已处理.
    /// </summary>
    /// <param name="kind">类型.</param>
    /// <param name="count">条数.</param>
    public void MarkProcessed(IngestKind kind, int count)
    {
        Interlocked.Add(ref _processed[(int)kind], count);
    }
}
