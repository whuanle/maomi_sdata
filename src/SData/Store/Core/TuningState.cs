using System.Globalization;
using Microsoft.Extensions.Logging;
using SData.Monitor;

namespace SData.Store;

/// <summary>InitializeAsync 未注入基线时的兜底值（正常路径由 StoreInitializer 传 TuningState 基线）.</summary>
internal static class TuningDefaults
{
    public const string MemoryLimit = "1GB";
    public const int Threads = 4;
    public const int MaxConcurrentQueries = 2;
}

/// <summary>
/// 自动调参快照（/api/monitor/status 的 Tuning 段；只读展示当前生效值与最近一次调整）.
/// </summary>
public sealed record TuningSnapshot
{
    /// <summary>有效 CPU 核数（min(ProcessorCount, cgroup CPU 配额)）.</summary>
    public int EffectiveCores { get; init; }

    /// <summary>环境内存上限（容器 cgroup memory.max 或主机内存）.</summary>
    public long MemoryLimitBytes { get; init; }

    /// <summary>DuckDB 内存限制（如 1GB）.</summary>
    public string DuckDbMemoryLimit { get; init; } = string.Empty;

    /// <summary>DuckDB 线程数.</summary>
    public int DuckDbThreads { get; init; }

    /// <summary>DuckDB 同时执行的查询数.</summary>
    public int DuckDbMaxConcurrentQueries { get; init; }

    /// <summary>接入缓冲当前条数容量（动态）.</summary>
    public int BufferCapacity { get; init; }

    /// <summary>接入缓冲当前字节预算（动态）.</summary>
    public long BufferByteBudgetBytes { get; init; }

    /// <summary>缓冲字节预算扩容硬顶（OOM 防线推导）.</summary>
    public long BufferByteBudgetCeilingBytes { get; init; }

    /// <summary>缓冲条数容量基线（缩容下限）.</summary>
    public int BufferCapacityBaseline { get; init; }

    /// <summary>缓冲字节预算基线（缩容下限）.</summary>
    public long BufferByteBudgetBaselineBytes { get; init; }

    /// <summary>批量落库条数（动态）.</summary>
    public int BatchSize { get; init; }

    /// <summary>落库间隔（毫秒）.</summary>
    public int FlushIntervalMs { get; init; }

    /// <summary>MCP 全局并发上限.</summary>
    public int AgentsGlobalConcurrency { get; init; }

    /// <summary>冷缓存目录容量上限.</summary>
    public long ColdCacheMaxBytes { get; init; }

    /// <summary>最近一次缓冲调整时间.</summary>
    public DateTimeOffset? LastAdjustAt { get; init; }

    /// <summary>最近一次调整原因.</summary>
    public string? LastAdjustReason { get; init; }

    /// <summary>启动以来调整总次数.</summary>
    public long AdjustCount { get; init; }
}

/// <summary>
/// 缓冲扩缩指令（AdaptiveTuningService 决策后应用；缩容只影响新准入，存量自然排空）.
/// </summary>
public sealed record BufferAdjustment(int Capacity, long ByteBudgetBytes, int BatchSize, int FlushIntervalMs, string Reason);

/// <summary>
/// 系统自动调参状态：性能参数的唯一真相源（不向用户暴露，system.json 残留键被忽略）.
/// 静态基线（DuckDB 内存/线程/并发、MCP 并发、冷缓存容量）启动时按环境一次算定；
/// 缓冲条数容量、字节预算、批量参数为动态值，由 <see cref="AdaptiveTuningService"/> 按负载扩缩，
/// 硬顶由环境内存上限推导，保证容器不被 OOM 杀死.
/// 默认值来自压测择优（tool/StressTest REPORT.md）：flush=300ms、DuckDB 内存=环境内存 50%（2g 容器 1GB）、
/// 批量 10k-50k 随核数扩展.
/// </summary>
public interface ITuningState
{
    /// <summary>当前生效值快照.</summary>
    TuningSnapshot Snapshot { get; }

    /// <summary>有效 CPU 核数.</summary>
    int EffectiveCores { get; }

    /// <summary>环境内存上限（字节）.</summary>
    long MemoryLimitBytes { get; }

    /// <summary>DuckDB 内存限制（TryNormalize 可解析的容量串）.</summary>
    string DuckDbMemoryLimit { get; }

    /// <summary>DuckDB 内存限制（字节）.</summary>
    long DuckDbMemoryLimitBytes { get; }

    /// <summary>DuckDB 线程数.</summary>
    int DuckDbThreads { get; }

    /// <summary>DuckDB 同时执行的查询数.</summary>
    int DuckDbMaxConcurrentQueries { get; }

    /// <summary>MCP 全局并发上限.</summary>
    int AgentsGlobalConcurrency { get; }

    /// <summary>MCP 单凭据并发上限.</summary>
    int AgentsPerKeyConcurrency { get; }

    /// <summary>冷缓存目录容量上限（字节）.</summary>
    long ColdCacheMaxBytes { get; }

    /// <summary>告警待投递上限.</summary>
    long AlertMaxPendingDeliveries { get; }

    /// <summary>缓冲字节预算扩容硬顶（字节）.</summary>
    long BufferByteBudgetCeilingBytes { get; }

    /// <summary>缓冲条数容量硬顶.</summary>
    int BufferCapacityCeiling { get; }

    /// <summary>缓冲当前条数容量（动态）.</summary>
    int BufferCapacity { get; }

    /// <summary>缓冲当前字节预算（动态）.</summary>
    long BufferByteBudgetBytes { get; }

    /// <summary>批量落库条数（动态）.</summary>
    int BatchSize { get; }

    /// <summary>落库间隔（毫秒，动态）.</summary>
    int FlushIntervalMs { get; }

    /// <summary>应用一次缓冲扩缩（AdaptiveTuningService 调用；容量/预算钳制到硬顶内）.</summary>
    void ApplyBufferAdjustment(BufferAdjustment adjustment);
}

/// <inheritdoc/>
public sealed class TuningState : ITuningState
{
    private const int FlushIntervalMsDefault = 300;

    private readonly ILogger<TuningState>? _logger;
    private readonly object _lock = new();
    private readonly int _effectiveCores;
    private readonly long _memoryLimitBytes;
    private readonly long _duckDbMemoryLimitBytes;
    private readonly int _duckDbThreads;
    private readonly int _duckDbMaxConcurrentQueries;
    private readonly int _agentsGlobalConcurrency;
    private readonly long _coldCacheMaxBytes;
    private readonly long _alertMaxPendingDeliveries;
    private readonly long _budgetCeiling;
    private readonly int _capacityCeiling;
    private readonly long _budgetBaseline;
    private readonly int _capacityBaseline;
    private int _bufferCapacity;
    private long _bufferBudget;
    private int _batchSize;
    private int _flushIntervalMs;
    private DateTimeOffset? _lastAdjustAt;
    private string? _lastAdjustReason;
    private long _adjustCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="TuningState"/> class.
    /// </summary>
    /// <param name="logger">日志（基线快照）.</param>
    public TuningState(ILogger<TuningState>? logger = null)
    {
        _logger = logger;
        // 环境输入：cgroup CPU 配额 + 容器/主机内存上限（无 cgroup 时用 GC 报告的可用内存，容器感知）
        var container = ContainerResources.Read();
        var gcLimit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var cpuLimit = container.CpuLimit;
        _effectiveCores = Math.Clamp(
            Math.Min(Environment.ProcessorCount, cpuLimit.HasValue ? (int)Math.Ceiling(cpuLimit.Value) : Environment.ProcessorCount),
            1, 64);
        _memoryLimitBytes = container.MemoryLimit
            ?? (gcLimit > 0 ? (long)gcLimit : 2L * 1024 * 1024 * 1024);

        // DuckDB：内存 = 环境内存的 50%（压测 2g 容器取 1GB），线程 = 有效核数，查询并发 = 核数一半（小内存防竞争）
        _duckDbMemoryLimitBytes = Math.Clamp(_memoryLimitBytes / 2, 256L * 1024 * 1024, 32L * 1024 * 1024 * 1024);
        _duckDbThreads = Math.Clamp(_effectiveCores, 1, 64);
        _duckDbMaxConcurrentQueries = Math.Clamp(_effectiveCores / 2, 1, 8);

        // MCP 并发随核数；冷缓存 = 环境内存 25%（磁盘缓存，上限兜底）；告警待投递随内存
        _agentsGlobalConcurrency = Math.Clamp(_effectiveCores, 2, 16);
        _coldCacheMaxBytes = Math.Clamp(_memoryLimitBytes / 4, 256L * 1024 * 1024, 4L * 1024 * 1024 * 1024);
        _alertMaxPendingDeliveries = Math.Clamp(_memoryLimitBytes / (1024 * 1024), 500, 10_000);

        // 缓冲基线：字节预算 = 环境内存 10%（64MB-1GB），条数 = 预算 / 平均 512B（50k-1M）
        _budgetBaseline = Math.Clamp(_memoryLimitBytes / 10, 64L * 1024 * 1024, 1024L * 1024 * 1024);
        _capacityBaseline = (int)Math.Clamp(_budgetBaseline / 512, 50_000, 1_000_000);
        // 硬顶（OOM 防线）：预算最高环境内存 30%（≤2GB）；条数硬顶按保守 256B/条推导
        _budgetCeiling = Math.Clamp(_memoryLimitBytes * 3 / 10, _budgetBaseline, 2L * 1024 * 1024 * 1024);
        _capacityCeiling = (int)Math.Clamp(_budgetCeiling / 256, _capacityBaseline, 4_000_000);

        _bufferCapacity = _capacityBaseline;
        _bufferBudget = _budgetBaseline;
        _batchSize = Math.Clamp(_effectiveCores * 5000, 10_000, 50_000);
        _flushIntervalMs = FlushIntervalMsDefault;

        _logger?.LogInformation(
            "Tuning baseline: cores={Cores}, memory_limit={MemoryLimitGiB}GiB, duckdb={DuckDbMemory}/threads={Threads}/queries={Queries}, buffer={Capacity}rows/{BudgetMiB}MiB(ceiling {CeilingCapacity}/{CeilingBudgetMiB}MiB), batch={Batch}, flush={Flush}ms",
            _effectiveCores, _memoryLimitBytes / (1024 * 1024 * 1024), FormatMemory(_duckDbMemoryLimitBytes), _duckDbThreads, _duckDbMaxConcurrentQueries,
            _bufferCapacity, _bufferBudget / (1024 * 1024), _capacityCeiling, _budgetCeiling / (1024 * 1024), _batchSize, _flushIntervalMs);
    }

    /// <inheritdoc/>
    public int EffectiveCores => _effectiveCores;

    /// <inheritdoc/>
    public long MemoryLimitBytes => _memoryLimitBytes;

    /// <inheritdoc/>
    public string DuckDbMemoryLimit => FormatMemory(_duckDbMemoryLimitBytes);

    /// <inheritdoc/>
    public long DuckDbMemoryLimitBytes => _duckDbMemoryLimitBytes;

    /// <inheritdoc/>
    public int DuckDbThreads => _duckDbThreads;

    /// <inheritdoc/>
    public int DuckDbMaxConcurrentQueries => _duckDbMaxConcurrentQueries;

    /// <inheritdoc/>
    public int AgentsGlobalConcurrency => _agentsGlobalConcurrency;

    /// <inheritdoc/>
    public int AgentsPerKeyConcurrency => 2;

    /// <inheritdoc/>
    public long ColdCacheMaxBytes => _coldCacheMaxBytes;

    /// <inheritdoc/>
    public long AlertMaxPendingDeliveries => _alertMaxPendingDeliveries;

    /// <inheritdoc/>
    public long BufferByteBudgetCeilingBytes => _budgetCeiling;

    /// <inheritdoc/>
    public int BufferCapacityCeiling => _capacityCeiling;

    /// <inheritdoc/>
    public int BufferCapacity { get { lock (_lock) { return _bufferCapacity; } } }

    /// <inheritdoc/>
    public long BufferByteBudgetBytes { get { lock (_lock) { return _bufferBudget; } } }

    /// <inheritdoc/>
    public int BatchSize { get { lock (_lock) { return _batchSize; } } }

    /// <inheritdoc/>
    public int FlushIntervalMs { get { lock (_lock) { return _flushIntervalMs; } } }

    /// <inheritdoc/>
    public TuningSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return new TuningSnapshot
                {
                    EffectiveCores = _effectiveCores,
                    MemoryLimitBytes = _memoryLimitBytes,
                    DuckDbMemoryLimit = FormatMemory(_duckDbMemoryLimitBytes),
                    DuckDbThreads = _duckDbThreads,
                    DuckDbMaxConcurrentQueries = _duckDbMaxConcurrentQueries,
                    BufferCapacity = _bufferCapacity,
                    BufferByteBudgetBytes = _bufferBudget,
                    BufferByteBudgetCeilingBytes = _budgetCeiling,
                    BufferCapacityBaseline = _capacityBaseline,
                    BufferByteBudgetBaselineBytes = _budgetBaseline,
                    BatchSize = _batchSize,
                    FlushIntervalMs = _flushIntervalMs,
                    AgentsGlobalConcurrency = _agentsGlobalConcurrency,
                    ColdCacheMaxBytes = _coldCacheMaxBytes,
                    LastAdjustAt = _lastAdjustAt,
                    LastAdjustReason = _lastAdjustReason,
                    AdjustCount = _adjustCount,
                };
            }
        }
    }

    /// <inheritdoc/>
    public void ApplyBufferAdjustment(BufferAdjustment adjustment)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        var capacity = Math.Clamp(adjustment.Capacity, 1, _capacityCeiling);
        var budget = Math.Clamp(adjustment.ByteBudgetBytes, 1, _budgetCeiling);
        var batch = Math.Clamp(adjustment.BatchSize, 1_000, 50_000);
        var flush = Math.Clamp(adjustment.FlushIntervalMs, 50, 5_000);
        lock (_lock)
        {
            if (capacity == _bufferCapacity && budget == _bufferBudget && batch == _batchSize && flush == _flushIntervalMs)
            {
                return;
            }

            _logger?.LogInformation(
                "Tuning adjust ({Reason}): buffer {OldCapacity}->{Capacity}rows/{OldBudget}->{BudgetMB}MB, batch {OldBatch}->{Batch}, flush {OldFlush}->{Flush}ms",
                adjustment.Reason, _bufferCapacity, capacity, _bufferBudget / (1024 * 1024), budget / (1024 * 1024), _batchSize, batch, _flushIntervalMs, flush);
            _bufferCapacity = capacity;
            _bufferBudget = budget;
            _batchSize = batch;
            _flushIntervalMs = flush;
            _lastAdjustAt = DateTimeOffset.UtcNow;
            _lastAdjustReason = adjustment.Reason;
            _adjustCount++;
        }
    }

    /// <summary>格式化为 DuckDbMemoryLimit.TryNormalize 可解析的容量串（二进制 GiB/MiB，与字节运算口径一致）.</summary>
    private static string FormatMemory(long bytes)
    {
        return bytes >= 1L << 30
            ? $"{(bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture)}GiB"
            : $"{Math.Max(1, (int)Math.Round(bytes / (double)(1L << 20)))}MiB";
    }
}
