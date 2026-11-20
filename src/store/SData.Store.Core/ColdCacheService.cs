using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SData.Infra;
using SData.Infra.Configuration;

namespace SData.Store.Core;

/// <summary>
/// 冷数据本地缓存（OpenObserve disk-cache 模式，单机版）：
/// 查询路径零阻塞——TryGetLocalPath 只查内存 LRU + 一次文件存在性检查；
/// 未命中的 S3 对象由查询侧 fire-and-forget Warm 入队，后台单线程串行下载（临时文件 → 原子落位）；
/// 每 10 分钟巡检一次容量，超 MaxBytes 按最久未访问淘汰；归档对象在归档时已做行数校验，缓存层信任对象内容.
/// 目录布局：{ContentRoot}/data/cache/cold/{domain}/{day}.parquet.
/// </summary>
public sealed class ColdCacheService : BackgroundService
{
    /// <summary>预热队列容量（满即丢弃，查询仍走 S3 直读，不阻塞）.</summary>
    public const int QueueCapacity = 64;

    private const string TempSuffix = ".tmp";

    private readonly SystemOptions _options;
    private readonly IObjectStore _objectStore;
    private readonly ILogger<ColdCacheService> _logger;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly object _lock = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private long _totalBytes;

    private sealed record CacheEntry(string Path, long Size, DateTime LastAccess);

    /// <summary>
    /// Initializes a new instance of the <see cref="ColdCacheService"/> class.
    /// </summary>
    /// <param name="options">系统配置（ColdCache 段）.</param>
    /// <param name="objectStore">对象存储（下载通道）.</param>
    /// <param name="logger">日志.</param>
    public ColdCacheService(SystemOptions options, IObjectStore objectStore, ILogger<ColdCacheService> logger)
    {
        _options = options;
        _objectStore = objectStore;
        _logger = logger;
    }

    /// <summary>
    /// 缓存命中返回本地文件路径（未命中返回 null；命中会刷新 LRU 访问时间）.
    /// </summary>
    /// <param name="objectKey">归档对象键.</param>
    /// <returns>本地缓存路径或 null.</returns>
    public string? TryGetLocalPath(string objectKey)
    {
        if (!_options.ColdCache.Enabled)
        {
            return null;
        }

        var cachePath = CachePath(objectKey);
        lock (_lock)
        {
            if (_entries.TryGetValue(objectKey, out var entry))
            {
                if (File.Exists(entry.Path))
                {
                    _entries[objectKey] = entry with { LastAccess = DateTime.UtcNow };
                    return entry.Path;
                }

                _entries.Remove(objectKey);
                _totalBytes -= entry.Size;
            }
        }

        // 进程重启后内存 LRU 为空：缓存文件仍在磁盘上，重建条目（幂等）
        if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
        {
            var size = new FileInfo(cachePath).Length;
            lock (_lock)
            {
                _entries[objectKey] = new CacheEntry(cachePath, size, DateTime.UtcNow);
                _totalBytes += size;
            }

            return cachePath;
        }

        return null;
    }

    /// <summary>
    /// 后台预热（fire-and-forget）：已命中或未启用时直接返回，否则入队等待串行下载.
    /// </summary>
    /// <param name="objectKey">归档对象键.</param>
    public void Warm(string objectKey)
    {
        if (!_options.ColdCache.Enabled || TryGetLocalPath(objectKey) != null)
        {
            return;
        }

        _queue.Writer.TryWrite(objectKey);
    }

    /// <summary>
    /// 失效并删除缓存文件（保留策略删除归档对象后调用，防止超期数据经缓存继续可读）.
    /// </summary>
    /// <param name="objectKey">归档对象键.</param>
    public void Invalidate(string objectKey)
    {
        var cachePath = CachePath(objectKey);
        lock (_lock)
        {
            if (_entries.Remove(objectKey, out var entry))
            {
                _totalBytes -= entry.Size;
            }
        }

        try
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "删除冷缓存文件失败（留待 LRU 巡检回收）：{CachePath}", cachePath);
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cold cache service started (enabled={Enabled}, maxBytes={MaxBytes})", _options.ColdCache.Enabled, _options.ColdCache.MaxBytes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _queue.Reader.WaitToReadAsync(stoppingToken);
                while (_queue.Reader.TryRead(out var objectKey))
                {
                    await DownloadAsync(objectKey, stoppingToken);
                }

                // 每批下载后检查容量（低于上限时为廉价判断；空闲期容量只增于下载/TryGet 重建两处，均已触发检查）
                Sweep();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Cold cache loop failed");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Cold cache service stopped");
    }

    private async Task DownloadAsync(string objectKey, CancellationToken cancellationToken)
    {
        if (TryGetLocalPath(objectKey) != null)
        {
            return;
        }

        var cachePath = CachePath(objectKey);
        var tempPath = cachePath + TempSuffix;
        try
        {
            await _objectStore.DownloadAsync(objectKey, tempPath, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.Move(tempPath, cachePath, overwrite: true);
            var size = new FileInfo(cachePath).Length;
            lock (_lock)
            {
                _entries[objectKey] = new CacheEntry(cachePath, size, DateTime.UtcNow);
                _totalBytes += size;
            }

            _logger.LogInformation("Cold cache warmed {ObjectKey} → {CachePath} ({Size} bytes)", objectKey, cachePath, size);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Cold cache warm failed (query keeps using direct read): {ObjectKey}", objectKey);
            TryDeleteFile(tempPath);
        }
    }

    private void Sweep()
    {
        lock (_lock)
        {
            if (_totalBytes <= _options.ColdCache.MaxBytes)
            {
                return;
            }

            // 超限按最久未访问淘汰（删除文件并移除条目）
            foreach (var pair in _entries.OrderBy(pair => pair.Value.LastAccess).ToList())
            {
                if (_totalBytes <= _options.ColdCache.MaxBytes)
                {
                    break;
                }

                _entries.Remove(pair.Key);
                _totalBytes -= pair.Value.Size;
                TryDeleteFile(pair.Value.Path);
                _logger.LogInformation("Cold cache evicted {CachePath} ({Size} bytes)", pair.Value.Path, pair.Value.Size);
            }
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "删除冷缓存文件失败：{Path}", path);
        }
    }

    private string CachePath(string objectKey)
    {
        // 取对象键最后两段作为缓存相对路径（{domain}/{day}.parquet）
        var segments = objectKey.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var relative = segments.Length >= 2
            ? $"{segments[^2]}/{segments[^1]}"
            : segments[^1];
        return Path.Combine(AppConst.GetPath(), "data", "cache", "cold", relative);
    }
}
