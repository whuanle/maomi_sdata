using System.Threading.Channels;
using DuckDB.NET.Data;
using SData.Infra.Configuration;
using SData.Store.Models;
using System.Text;

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
    private readonly long _byteBudgetBytes;
    private long _estimatedBytes;
    private IngestWriterFailure? _writerFailure;
    private int _persistWaiters;

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
        _byteBudgetBytes = Math.Max(1L, systemOptions.Ingest.BufferByteBudgetBytes);
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
        var estimated = EstimateRecord(record);
        if (!TryReserveBytes(estimated))
        {
            Interlocked.Increment(ref _dropped[(int)kind]);
            return false;
        }

        if (_channel.Writer.TryWrite(new IngestItem(kind, record)))
        {
            return true;
        }

        ReleaseBufferedBytes(estimated);
        Interlocked.Increment(ref _dropped[(int)kind]);
        return false;
    }

    private (long Accepted, long Dropped, IReadOnlyList<T> Rejected) TryEnqueueBatch<T>(IngestKind kind, IReadOnlyList<T> records)
    {
        long accepted = 0;
        long dropped = 0;
        var rejected = new List<T>();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i]!;
            var estimated = EstimateRecord(record);
            if (!TryReserveBytes(estimated))
            {
                Interlocked.Increment(ref _dropped[(int)kind]);
                dropped++;
                rejected.Add(records[i]);
                continue;
            }

            if (!_channel.Writer.TryWrite(new IngestItem(kind, record)))
            {
                ReleaseBufferedBytes(estimated);
                Interlocked.Increment(ref _dropped[(int)kind]);
                dropped++;
                rejected.Add(records[i]);
                continue;
            }

            accepted++;
        }

        return (accepted, dropped, rejected);
    }

    /// <inheritdoc/>
    public async Task PersistLogsAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        Interlocked.Increment(ref _persistWaiters);
        try
        {
            // 单读者 FIFO 且批次顺序落库：最后一条落库即代表整批已提交，只需一个回执
            var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            for (var i = 0; i < records.Count; i++)
            {
                var estimated = EstimateRecord(records[i]);
                await WaitReserveBytesAsync(estimated, cancellationToken);
                var item = i == records.Count - 1
                    ? new IngestItem(IngestKind.Log, records[i], receipt)
                    : new IngestItem(IngestKind.Log, records[i]);
                try
                {
                    await _channel.Writer.WriteAsync(item, cancellationToken);
                }
                catch
                {
                    ReleaseBufferedBytes(estimated);
                    throw;
                }
            }

            await receipt.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _persistWaiters);
        }
    }

    /// <summary>有调用方正在等待落库回执（写入器应立即刷写，不做低水位聚合等待）.</summary>
    public bool HasPersistWaiters => Volatile.Read(ref _persistWaiters) > 0;

    /// <inheritdoc/>
    public bool TryEnqueueLog(LogRecord record) => TryEnqueue(IngestKind.Log, record);

    /// <inheritdoc/>
    public (long Accepted, long Dropped, IReadOnlyList<LogRecord> Rejected) TryEnqueueLogs(IReadOnlyList<LogRecord> records)
        => TryEnqueueBatch(IngestKind.Log, records);

    /// <inheritdoc/>
    public bool TryEnqueueMqConsumption(MqConsumptionRecord record) => TryEnqueue(IngestKind.MqConsumption, record);

    /// <inheritdoc/>
    public bool TryEnqueueSpan(SpanRecord record) => TryEnqueue(IngestKind.Trace, record);

    /// <inheritdoc/>
    public (long Accepted, long Dropped, IReadOnlyList<SpanRecord> Rejected) TryEnqueueSpans(IReadOnlyList<SpanRecord> records)
        => TryEnqueueBatch(IngestKind.Trace, records);

    /// <inheritdoc/>
    public bool TryEnqueueMetric(MetricPointRecord record) => TryEnqueue(IngestKind.Metric, record);

    /// <inheritdoc/>
    public (long Accepted, long Dropped, IReadOnlyList<MetricPointRecord> Rejected) TryEnqueueMetrics(IReadOnlyList<MetricPointRecord> records)
        => TryEnqueueBatch(IngestKind.Metric, records);

    /// <inheritdoc/>
    public bool TryEnqueueTelemetry(TelemetryRecord record) => TryEnqueue(IngestKind.Telemetry, record);

    /// <inheritdoc/>
    public (long Accepted, long Dropped, IReadOnlyList<TelemetryRecord> Rejected) TryEnqueueTelemetry(IReadOnlyList<TelemetryRecord> records)
        => TryEnqueueBatch(IngestKind.Telemetry, records);

    /// <inheritdoc/>
    public bool TryEnqueueRumEvent(RumEventRecord record) => TryEnqueue(IngestKind.RumEvent, record);

    /// <inheritdoc/>
    public bool TryEnqueueRumSession(RumSessionRecord record) => TryEnqueue(IngestKind.RumSession, record);

    /// <inheritdoc/>
    public bool TryEnqueueRumReplay(RumReplayRecord record) => TryEnqueue(IngestKind.RumReplay, record);

    /// <inheritdoc/>
    public bool CanAcceptBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return true;
        }

        var current = Volatile.Read(ref _estimatedBytes);
        return current <= _byteBudgetBytes
            && bytes <= _byteBudgetBytes - current;
    }

    private bool TryReserveBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return true;
        }

        while (true)
        {
            var current = Volatile.Read(ref _estimatedBytes);
            if (current > _byteBudgetBytes || bytes > _byteBudgetBytes - current)
            {
                return false;
            }

            var next = current + bytes;
            if (Interlocked.CompareExchange(ref _estimatedBytes, next, current) == current)
            {
                return true;
            }
        }
    }

    private async Task WaitReserveBytesAsync(long bytes, CancellationToken cancellationToken)
    {
        while (!TryReserveBytes(bytes))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private void ReleaseBufferedBytes(long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref _estimatedBytes, -bytes);
        }
    }

    private static long EstimateRecord(object record)
    {
        const long fixedCost = 64;
        return record switch
        {
            LogRecord log => fixedCost
                + Utf8(log.Service) + Utf8(log.Severity) + Utf8(log.Body)
                + Utf8(log.TraceId) + Utf8(log.SpanId) + Utf8(log.AttributesJson)
                + Utf8(log.ResourceJson) + Utf8(log.Source),
            SpanRecord span => fixedCost
                + Utf8(span.TraceId) + Utf8(span.SpanId) + Utf8(span.ParentSpanId)
                + Utf8(span.Service) + Utf8(span.Name) + Utf8(span.Kind) + Utf8(span.Status)
                + Utf8(span.AttributesJson) + Utf8(span.EventsJson) + Utf8(span.ResourceJson),
            MetricPointRecord metric => fixedCost + Utf8(metric.Name)
                + Utf8(metric.LabelsJson) + Utf8(metric.Kind),
            TelemetryRecord telemetry => fixedCost + Utf8(telemetry.DeviceKey)
                + Utf8(telemetry.Signal) + Utf8(telemetry.StrValue),
            _ => fixedCost + 128,
        };
    }

    private static long Utf8(string? value)
    {
        return string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);
    }

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

        stats.BufferedBytes = Volatile.Read(ref _estimatedBytes);
        stats.ByteBudgetBytes = _byteBudgetBytes;

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

    /// <summary>
    /// 写入器标记一批已成功落库，并释放对应载荷字节预算.
    /// </summary>
    public void MarkProcessed(IngestKind kind, IReadOnlyList<object> records)
    {
        long released = 0;
        foreach (var record in records)
        {
            released += EstimateRecord(record);
        }

        ReleaseBufferedBytes(released);
        Interlocked.Add(ref _processed[(int)kind], records.Count);
    }
}
