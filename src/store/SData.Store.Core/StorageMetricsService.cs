using System.Globalization;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Settings;

namespace SData.Store;

/// <summary>本地与远端采样独立运行；所有 GET 只读取缓存快照.</summary>
public sealed class StorageMetricsService(
    DuckDbConnectionManager connections,
    SystemOptions options,
    ISystemSettingsProvider settings,
    ILogger<StorageMetricsService> logger) : BackgroundService, IStorageMetrics
{
    private LocalStorageSnapshot _local = new();
    private readonly S3MetricsSampler _s3Sampler = new(settings);

    public LocalStorageSnapshot Local => Volatile.Read(ref _local);

    public S3StorageSnapshot S3 => _s3Sampler.Snapshot;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(LocalLoopAsync(stoppingToken), S3LoopAsync(stoppingToken));

    private async Task LocalLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try { Volatile.Write(ref _local, await SampleLocalAsync(token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Local storage sampling failed ({ErrorType})", exception.GetType().Name);
                Volatile.Write(ref _local, Local with { State = "error" });
            }
        }
        while (await timer.WaitForNextTickAsync(token));
    }

    public async Task<LocalStorageSnapshot> SampleLocalAsync(CancellationToken token)
    {
        var path = Path.GetFullPath(Path.Combine(AppConst.GetPath(), options.DuckDb.Path));
        var temp = string.IsNullOrWhiteSpace(options.DuckDb.TempDirectory)
            ? path + ".tmp"
            : Path.GetFullPath(Path.Combine(AppConst.GetPath(), options.DuckDb.TempDirectory));
        var result = Local with { SampledAt = DateTimeOffset.UtcNow, State = "ok" };
        try
        {
            using var connection = connections.CloneConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT tag, memory_usage_bytes, temporary_storage_bytes FROM duckdb_memory()";
            await using var reader = await command.ExecuteReaderAsync(token);
            long memory = 0, temporary = 0;
            var categories = new List<string>();
            while (await reader.ReadAsync(token))
            {
                var bytes = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                var spill = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                memory += bytes;
                temporary += spill;
                if (bytes > 0 || spill > 0) categories.Add($"{reader.GetString(0)}={bytes}/{spill}");
            }
            await reader.DisposeAsync();
            command.CommandText = "SELECT current_setting('memory_limit'), current_setting('threads')";
            await using var config = await command.ExecuteReaderAsync(token);
            await config.ReadAsync(token);
            result = result with
            {
                DuckDbState = "ok", DuckDbSampledAt = DateTimeOffset.UtcNow, TrackedMemoryBytes = memory, TemporaryBytes = temporary,
                MemoryLimit = config.GetString(0), Threads = Convert.ToInt32(config.GetValue(1), CultureInfo.InvariantCulture),
            };
            logger.LogDebug("DuckDB resource sample (tracked={TrackedBytes}, spill={SpillBytes}, limit={MemoryLimit}, threads={Threads}, categories={MemoryCategories})",
                memory, temporary, result.MemoryLimit, result.Threads, string.Join(",", categories));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("DuckDB metrics unavailable ({ErrorType})", exception.GetType().Name);
            result = result with { DuckDbState = "error", State = "partial" };
        }

        try
        {
            result = result with { FileState = "ok", FileSampledAt = DateTimeOffset.UtcNow, DatabaseBytes = FileBytes(path), WalBytes = FileBytes(path + ".wal") };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result = result with { FileState = "error", State = "partial" };
        }

        var volumes = DriveInfo.GetDrives().Select(x => x.Name).ToArray();
        var disks = new[] { path, temp }.Select(x => FindVolume(x, volumes)).Distinct().Select(volume =>
        {
            try
            {
                if (volume == null) return new DiskSnapshot { State = "unsupported" };
                var drive = new DriveInfo(volume);
                return new DiskSnapshot { Volume = volume, State = "ok", TotalBytes = drive.TotalSize, AvailableBytes = drive.AvailableFreeSpace };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new DiskSnapshot { Volume = volume ?? string.Empty, State = "error" };
            }
        }).ToArray();
        return result with { Disks = disks, State = disks.Any(x => x.State != "ok") ? "partial" : result.State };
    }

    private static long FileBytes(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
    }

    public static string? FindVolume(string path, IEnumerable<string> volumes)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return volumes.OrderByDescending(x => x.Length).FirstOrDefault(volume =>
        {
            var normalized = path.Replace('\\', '/');
            var root = volume.Replace('\\', '/').TrimEnd('/');
            return normalized.Equals(root, comparison) || normalized.StartsWith(root + "/", comparison);
        });
    }

    private async Task S3LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await _s3Sampler.SampleIfDueAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning("S3 sampler failed ({ErrorType})", exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(token));
    }

    public static async Task<(long Bytes, long Objects)> ListSizeAsync(IAmazonS3 client, string bucket, string prefix, CancellationToken token)
    {
        long bytes = 0;
        long objects = 0;
        string? continuation = null;
        var boundary = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim('/') + "/";
        do
        {
            var page = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket, Prefix = boundary, ContinuationToken = continuation,
            }, token);
            foreach (var item in page.S3Objects ?? [])
            {
                bytes = checked(bytes + (item.Size ?? 0));
                objects++;
            }

            if (page.IsTruncated != true) return (bytes, objects);
            var next = page.NextContinuationToken;
            if (string.IsNullOrEmpty(next) || next == continuation) throw new InvalidOperationException("Invalid continuation token");
            continuation = next;
        }
        while (true);
    }
}
