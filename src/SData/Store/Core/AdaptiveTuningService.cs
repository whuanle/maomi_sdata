using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SData.Monitor;
using SData.Store.Models;

namespace SData.Store;

/// <summary>
/// 自适应调参后台服务：每 10 秒观察资源快照与缓冲水位.
/// 缓冲接近满载（或发生丢弃）且内存有余量时逐步扩容，受「进程占用 + DuckDB 限额 + 缓冲预算 ≤
/// 环境内存 × 85%」的 OOM 硬顶约束（避免容器被杀死）；持续空闲时按步回缩到基线。
/// 高水位连续 2 个 tick 才扩容、缩容需持续空闲 5 分钟，防止震荡。
/// DuckDB 内存/线程/并发等静态基线已由 TuningState 启动算定，此处不做运行中调整。
/// </summary>
public sealed class AdaptiveTuningService : BackgroundService
{
    private const int TickSeconds = 10;
    private const double GrowUtilization = 0.8;
    private const double ShrinkUtilization = 0.1;
    private static readonly TimeSpan ShrinkAfterIdle = TimeSpan.FromMinutes(5);
    private const double MemorySafetyFactor = 0.85;
    private const double GrowFactor = 1.25;
    private const double ShrinkFactor = 0.75;
    private const int GrowHighTicksRequired = 2;

    private readonly ITuningState _tuning;
    private readonly IngestBuffer _buffer;
    private readonly IResourceSnapshotProvider _resources;
    private readonly ILogger<AdaptiveTuningService> _logger;

    private long _lastDropped;
    private int _highTicks;
    private DateTimeOffset? _idleSince;
    private DateTimeOffset _lastAdjustAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdaptiveTuningService"/> class.
    /// </summary>
    /// <param name="tuning">自动调参状态.</param>
    /// <param name="buffer">接入缓冲.</param>
    /// <param name="resources">资源快照提供器.</param>
    /// <param name="logger">日志.</param>
    public AdaptiveTuningService(ITuningState tuning, IngestBuffer buffer, IResourceSnapshotProvider resources, ILogger<AdaptiveTuningService> logger)
    {
        _tuning = tuning;
        _buffer = buffer;
        _resources = resources;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Adaptive tuning started (tick={TickSeconds}s, grow≥{Grow:P0}×{Ticks}ticks, shrink≤{Shrink:P0} idle {Idle}m, memory safety {Safety:P0})",
            TickSeconds, GrowUtilization, GrowHighTicksRequired, ShrinkUtilization, ShrinkAfterIdle.TotalMinutes, MemorySafetyFactor);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TickSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                Tick(DateTimeOffset.UtcNow);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Adaptive tuning tick failed");
            }
        }
    }

    private void Tick(DateTimeOffset now)
    {
        var stats = _buffer.GetStats();
        var dropped = stats.Dropped.Values.Sum();
        var droppedDelta = dropped - _lastDropped;
        _lastDropped = dropped;

        // 调整冷却：一个 tick 周期内最多一次
        if (now - _lastAdjustAt < TimeSpan.FromSeconds(TickSeconds))
        {
            return;
        }

        var capacity = Math.Max(1, _buffer.Capacity);
        var countUtil = (double)_buffer.ApproximateBufferedCount / capacity;
        var byteUtil = stats.ByteBudgetBytes > 0 ? (double)stats.BufferedBytes / stats.ByteBudgetBytes : 0;
        var utilization = Math.Max(countUtil, byteUtil);

        // —— 扩容：丢弃发生或水位 ≥80% 且内存有余量 ——
        var high = droppedDelta > 0 || utilization >= GrowUtilization;
        _highTicks = high ? _highTicks + 1 : 0;
        if (high && _highTicks >= GrowHighTicksRequired)
        {
            if (TryGrow(stats, droppedDelta, now))
            {
                _highTicks = 0;
                _idleSince = null;
                _lastAdjustAt = now;
            }
            else
            {
                // 内存不足或已到硬顶：退一格，下个 tick 再探（避免每 tick 重复计算）
                _highTicks = GrowHighTicksRequired - 1;
            }

            return;
        }

        // —— 缩容：持续空闲 5 分钟后按步回缩到基线 ——
        if (utilization <= ShrinkUtilization)
        {
            _idleSince ??= now;
            if (now - _idleSince.Value >= ShrinkAfterIdle)
            {
                TryShrink(now);
            }
        }
        else
        {
            _idleSince = null;
        }
    }

    /// <returns>true = 已扩容.</returns>
    private bool TryGrow(IngestBufferStats stats, long droppedDelta, DateTimeOffset now)
    {
        var budget = Math.Max(stats.ByteBudgetBytes, 1);
        var newBudget = Math.Min((long)(budget * GrowFactor), _tuning.BufferByteBudgetCeilingBytes);
        if (newBudget <= budget)
        {
            return false; // 已在硬顶
        }

        // OOM 硬顶：进程私有内存 + DuckDB 限额 + 新缓冲预算 ≤ 环境内存 × 安全系数
        var snapshot = _resources.Latest;
        var privateBytes = snapshot.PrivateMemoryBytes ?? snapshot.WorkingSetBytes ?? 0;
        var memLimit = snapshot.ContainerMemoryLimitBytes ?? _tuning.MemoryLimitBytes;
        var projected = privateBytes + _tuning.DuckDbMemoryLimitBytes + newBudget;
        if (projected > (long)(memLimit * MemorySafetyFactor))
        {
            _logger.LogWarning("Tuning grow blocked: projected {ProjectedMB}MB > limit {LimitMB}MB × {Factor:P0} (private={PrivateMB}MB, duckdb={DuckDbMB}MB, buffer={BufferMB}MB)",
                projected / (1024 * 1024), memLimit / (1024 * 1024), MemorySafetyFactor, privateBytes / (1024 * 1024), _tuning.DuckDbMemoryLimitBytes / (1024 * 1024), newBudget / (1024 * 1024));
            return false;
        }

        var newCapacity = (int)Math.Min((long)(_buffer.Capacity * GrowFactor), _tuning.BufferCapacityCeiling);
        // 丢弃压力下提高批量上限（更大批次摊薄 commit 成本）；间隔保持不变
        var currentBatch = _tuning.BatchSize;
        var newBatch = droppedDelta > 0 ? Math.Min(currentBatch * 2, 50_000) : currentBatch;
        var reason = droppedDelta > 0 ? $"dropped={droppedDelta}, util high" : "buffer utilization high";
        _tuning.ApplyBufferAdjustment(new BufferAdjustment(newCapacity, newBudget, newBatch, _tuning.FlushIntervalMs, reason));
        _buffer.Resize(newCapacity, newBudget);
        _logger.LogInformation("Buffer grown to {Capacity} rows / {BudgetMB}MB (batch={Batch}) — {Reason}",
            newCapacity, newBudget / (1024 * 1024), newBatch, reason);
        return true;
    }

    private void TryShrink(DateTimeOffset now)
    {
        var snapshot = _tuning.Snapshot;
        var budget = _buffer.Capacity > 0 ? Math.Max(snapshot.BufferByteBudgetBytes, 1) : snapshot.BufferByteBudgetBaselineBytes;
        var newBudget = Math.Max((long)(budget * ShrinkFactor), snapshot.BufferByteBudgetBaselineBytes);
        var newCapacity = Math.Max((int)(_buffer.Capacity * ShrinkFactor), snapshot.BufferCapacityBaseline);
        if (newBudget >= budget && newCapacity >= _buffer.Capacity)
        {
            _idleSince = now; // 已在基线，重新计时
            return;
        }

        var newBatch = Math.Max((int)(snapshot.BatchSize * ShrinkFactor), 10_000);
        _tuning.ApplyBufferAdjustment(new BufferAdjustment(newCapacity, newBudget, newBatch, snapshot.FlushIntervalMs, "idle shrink"));
        _buffer.Resize(newCapacity, newBudget);
        _logger.LogInformation("Buffer shrunk to {Capacity} rows / {BudgetMB}MB (batch={Batch}) — idle 5m",
            newCapacity, newBudget / (1024 * 1024), newBatch);
        _idleSince = now;
        _lastAdjustAt = now;
    }
}
