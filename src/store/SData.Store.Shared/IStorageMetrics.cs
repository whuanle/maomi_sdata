namespace SData.Store;

/// <summary>只读存储采样；请求只读取缓存，不触发网络或数据库扫描.</summary>
public interface IStorageMetrics
{
    LocalStorageSnapshot Local { get; }
    S3StorageSnapshot S3 { get; }
}

public sealed record DiskSnapshot
{
    public string Volume { get; init; } = string.Empty;
    public string State { get; init; } = "unknown";
    public long? TotalBytes { get; init; }
    public long? AvailableBytes { get; init; }
}

public sealed record LocalStorageSnapshot
{
    public string State { get; init; } = "unknown";
    public DateTimeOffset? SampledAt { get; init; }
    public string DuckDbState { get; init; } = "unknown";
    public DateTimeOffset? DuckDbSampledAt { get; init; }
    public long? TrackedMemoryBytes { get; init; }
    public long? TemporaryBytes { get; init; }
    public string? MemoryLimit { get; init; }
    public int? Threads { get; init; }
    public string FileState { get; init; } = "unknown";
    public DateTimeOffset? FileSampledAt { get; init; }
    public long? DatabaseBytes { get; init; }
    public long? WalBytes { get; init; }
    public IReadOnlyList<DiskSnapshot> Disks { get; init; } = [];
}

public sealed record S3StorageSnapshot
{
    public string State { get; init; } = "unknown";
    public DateTimeOffset? CheckedAt { get; init; }
    public DateTimeOffset? SampledAt { get; init; }
    public string Bucket { get; init; } = string.Empty;
    public string Prefix { get; init; } = string.Empty;
    public long? Bytes { get; init; }
    public long? Objects { get; init; }
}
