using System.Threading.Channels;
using DuckDB.NET.Data;
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
    /// <param name="persisted">可选的持久化完成回执.</param>
    /// <param name="counted">是否占用条数闸门（MQ 持久化路径仅受字节预算约束，不占条数）.</param>
    public IngestItem(IngestKind kind, object record, TaskCompletionSource? persisted = null, bool counted = true)
    {
        Kind = kind;
        Record = record;
        Persisted = persisted;
        Counted = counted;
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

    /// <summary>是否占用条数闸门（写入器读出后按此释放）.</summary>
    public bool Counted { get; }
}

/// <summary>
/// 接入内存缓冲实现（动态闸门：条数 + 字节预算双 CAS，容量随 AdaptiveTuningService 扩缩；
/// 通道本身以「硬顶 × 2」作溢出安全网，正常流量由动态闸门拒绝）.
/// </summary>
public class IngestBuffer : IIngestBuffer
{
    private readonly Channel<IngestItem> _channel;
    private int _capacity;
    private long _byteBudgetBytes;
    private long _estimatedBytes;
    private int _count;
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
    /// <param name="tuning">自动调参状态（初始容量/预算取基线，后续随扩缩调整）.</param>
    public IngestBuffer(ITuningState tuning)
    {
        // 安全网容量 = 条数硬顶 × 2：正常流量由动态闸门拒绝，通道满仅是极端兜底
        _channel = Channel.CreateBounded<IngestItem>(new BoundedChannelOptions(Math.Max(1024, tuning.BufferCapacityCeiling * 2))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        Volatile.Write(ref _capacity, Math.Max(1, tuning.BufferCapacity));
        Volatile.Write(ref _byteBudgetBytes, Math.Max(1L, tuning.BufferByteBudgetBytes));
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

        if (!TryReserveSlot())
        {
            ReleaseBufferedBytes(estimated);
            Interlocked.Increment(ref _dropped[(int)kind]);
            return false;
        }

        if (_channel.Writer.TryWrite(new IngestItem(kind, record)))
        {
            return true;
        }

        ReleaseSlot();
        ReleaseBufferedBytes(estimated);
        Interlocked.Increment(ref _dropped[(int)kind]);
        return false;
    }

    private (long Accepted, long Dropped, IReadOnlyList<T> Rejected) TryEnqueueBatch<T>(IngestKind kind, IReadOnlyList<T> records)
    {
        long accepted = 0;
        long dropped = 0;
        var rejected = new List<T>();
        if (records.Count == 0)
        {
            return (0, 0, rejected);
        }

        if (records.Count >= Capacity)
        {
            // 批次不小于动态容量：永远无法整批接受，退回逐条语义避免客户端无限重试
            for (var i = 0; i < records.Count; i++)
            {
                if (TryEnqueue(kind, records[i]!))
                {
                    accepted++;
                }
                else
                {
                    dropped++;
                    rejected.Add(records[i]!);
                }
            }

            return (accepted, dropped, rejected);
        }

        // 先在闸门外估算字节（EstimateRecord 结果缓存在记录上，纯计算）
        var estimates = new long[records.Count];
        long totalEstimated = 0;
        for (var i = 0; i < records.Count; i++)
        {
            estimates[i] = EstimateRecord(records[i]!);
            totalEstimated += estimates[i];
        }

        // 整批原子：名额与字节都放得下才入队；否则整批拒绝（accepted=0）。
        // 客户端对 429 整批重试时不会与已接受部分重复（压测实测消除 0.68% 的重复落库）。
        if (!TryReserveSlots(records.Count))
        {
            Interlocked.Add(ref _dropped[(int)kind], records.Count);
            rejected.AddRange(records);
            return (0, records.Count, rejected);
        }

        if (!TryReserveBytes(totalEstimated))
        {
            ReleaseSlots(records.Count);
            Interlocked.Add(ref _dropped[(int)kind], records.Count);
            rejected.AddRange(records);
            return (0, records.Count, rejected);
        }

        for (var i = 0; i < records.Count; i++)
        {
            if (!_channel.Writer.TryWrite(new IngestItem(kind, records[i]!)))
            {
                // 通道硬顶为容量×2，名额已预留时理论不可达；防御性回滚
                ReleaseSlot();
                ReleaseBufferedBytes(estimates[i]);
                Interlocked.Increment(ref _dropped[(int)kind]);
                dropped++;
                rejected.Add(records[i]!);
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
                    ? new IngestItem(IngestKind.Log, records[i], receipt, counted: false)
                    : new IngestItem(IngestKind.Log, records[i], counted: false);
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

    /// <summary>运行时调整条数容量与字节预算（缩容只影响新准入，存量自然排空）.</summary>
    public void Resize(int capacityItems, long byteBudgetBytes)
    {
        Volatile.Write(ref _capacity, Math.Max(1, capacityItems));
        Volatile.Write(ref _byteBudgetBytes, Math.Max(1L, byteBudgetBytes));
    }

    /// <summary>写入器释放已读出条目的条数闸门（仅 TryEnqueue 路径占用的条目计数）.</summary>
    public void ReleaseSlots(int countedCount)
    {
        if (countedCount > 0)
        {
            Interlocked.Add(ref _count, -countedCount);
        }
    }

    /// <summary>释放单个条数闸门（入队失败回滚）.</summary>
    private void ReleaseSlot() => Interlocked.Decrement(ref _count);

    /// <inheritdoc/>
    public bool CanAcceptBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return true;
        }

        var budget = Volatile.Read(ref _byteBudgetBytes);
        var current = Volatile.Read(ref _estimatedBytes);
        return current <= budget
            && bytes <= budget - current;
    }

    /// <inheritdoc/>
    public int ApproximateBufferedCount => Volatile.Read(ref _count);

    /// <inheritdoc/>
    public int Capacity => Volatile.Read(ref _capacity);

    /// <summary>条数闸门 CAS 预留（满即拒绝；容量动态可调）.</summary>
    private bool TryReserveSlot()
    {
        while (true)
        {
            var current = Volatile.Read(ref _count);
            if (current >= Volatile.Read(ref _capacity))
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _count, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>条数闸门 CAS 批量预留（整批原子：名额不足则整批拒绝）.</summary>
    private bool TryReserveSlots(int count)
    {
        while (true)
        {
            var current = Volatile.Read(ref _count);
            if (current + count > Volatile.Read(ref _capacity))
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _count, current + count, current) == current)
            {
                return true;
            }
        }
    }

    private bool TryReserveBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return true;
        }

        while (true)
        {
            var budget = Volatile.Read(ref _byteBudgetBytes);
            var current = Volatile.Read(ref _estimatedBytes);
            if (current > budget || bytes > budget - current)
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
        if (record is IEstimatedSize cached)
        {
            var cachedValue = cached.EstimatedBytesCache;
            if (cachedValue >= 0)
            {
                return cachedValue;
            }
        }

        var estimated = record switch
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
        if (record is IEstimatedSize cacheable)
        {
            cacheable.EstimatedBytesCache = estimated;
        }

        return estimated;
    }

    /// <summary>缓冲字节估算可缓存的记录.</summary>
    private interface IEstimatedSize
    {
        /// <summary>估算缓存；-1 = 未计算.</summary>
        long EstimatedBytesCache { get; set; }
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
        stats.ByteBudgetBytes = Volatile.Read(ref _byteBudgetBytes);

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
