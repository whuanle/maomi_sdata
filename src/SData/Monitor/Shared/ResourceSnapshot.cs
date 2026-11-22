namespace SData.Monitor;

/// <summary>系统可见资源与应用进程快照；空值表示不可获取或等待首次差分采样.</summary>
public sealed record ResourceSnapshot
{
    public string State { get; init; } = "unknown";
    public DateTimeOffset? SampledAt { get; init; }
    public string Os { get; init; } = string.Empty;
    public string OsArchitecture { get; init; } = string.Empty;
    public string ProcessArchitecture { get; init; } = string.Empty;
    public string Runtime { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public double? SystemCpuPercent { get; init; }
    public double? ProcessCpuPercent { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public long? AvailableMemoryBytes { get; init; }
    public long? WorkingSetBytes { get; init; }
    public long? PrivateMemoryBytes { get; init; }
    public long? GcHeapBytes { get; init; }
    /// <summary>Gen0/1/2 GC 累计回收次数（进程启动以来，配合 Prometheus rate 计算速率）.</summary>
    public long GcCollectionCount0 { get; init; }
    public long GcCollectionCount1 { get; init; }
    public long GcCollectionCount2 { get; init; }
    public int? ProcessThreads { get; init; }
    public int? ThreadPoolThreads { get; init; }
    public long? PendingWorkItems { get; init; }
    public long? ContainerMemoryLimitBytes { get; init; }
    public long? ContainerMemoryBytes { get; init; }
    public double? ContainerCpuLimit { get; init; }
}
