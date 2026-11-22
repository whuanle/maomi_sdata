namespace SData.Store;

/// <summary>进程内 Store 性能采样；快照不触发数据库访问.</summary>
public interface IStorePerformanceMetrics
{
    StorePerformanceSnapshot Snapshot { get; }

    /// <summary>记录接入背压快速拒绝次数（模型绑定前拦截的 429）.</summary>
    void RecordIngestFastRejected(long count);
}

/// <summary>Store 累积直方图的只读快照.</summary>
public sealed record StorePerformanceSnapshot
{
    public IReadOnlyList<StoreHistogramSnapshot> Histograms { get; init; } = [];
    public IReadOnlyList<StoreCounterSnapshot> Counters { get; init; } = [];
}

/// <summary>Store 累积工作量计数器.</summary>
public sealed record StoreCounterSnapshot
{
    public string Name { get; init; } = string.Empty;
    public long Value { get; init; }
}

/// <summary>Prometheus 直方图的边界、桶数、总样本数及累计耗时.</summary>
public sealed record StoreHistogramSnapshot
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<double> UpperBoundsSeconds { get; init; } = [];
    public IReadOnlyList<long> BucketCounts { get; init; } = [];
    public long Count { get; init; }
    public double SumSeconds { get; init; }
}
