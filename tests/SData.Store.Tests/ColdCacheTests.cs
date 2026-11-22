using global::Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Settings.Services;
using SData.Store;
using SData.Store.Core;

namespace SData.Store.Tests;

/// <summary>
/// 冷数据本地缓存测试（T515）：本地对象预热命中、LRU 容量淘汰最久未访问条目.
/// </summary>
[Collection("content-root-serial")]
public class ColdCacheTests
{
    /// <summary>本地对象 Warm 后缓存命中（路径指向 data/cache/cold 镜像）；LRU 容量满时淘汰最旧条目.</summary>
    [Fact]
    public async Task Warm_LocalObject_HitThenLruEviction()
    {
        var temp = Path.Combine(Path.GetTempPath(), "sdata-coldcache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        AppConst.ContentRoot = temp;
        try
        {
            var options = new SystemOptions();
            options.ColdCache.MaxBytes = 1L * 1024 * 1024 * 1024; // 先给足容量
            var objectStore = new CompositeObjectStore(new LocalObjectStore(), new S3ObjectStore(options, NullLogger<S3ObjectStore>.Instance));
            using var cache = new ColdCacheService(SystemSettingsProvider.FileOnly(options), objectStore, NullLogger<ColdCacheService>.Instance);
            await cache.StartAsync(CancellationToken.None);

            var keyA = "data/cold/logs/20200101.parquet";
            var keyB = "data/cold/logs/20200102.parquet";
            var payloadA = new byte[] { 1, 2, 3, 4, 5 };
            var payloadB = new byte[] { 9, 8, 7 };
            WriteColdObject(temp, keyA, payloadA);
            WriteColdObject(temp, keyB, payloadB);

            // Warm A → 后台复制 → 命中本地缓存路径
            cache.Warm(keyA);
            var pathA = AwaitHit(cache, keyA);
            Assert.NotNull(pathA);
            Assert.Contains(Path.Combine("data", "cache", "cold", "logs"), pathA);

            // Warm B 后把容量压到 B 的体积：LRU 淘汰最久未访问的 A，B 存活
            options.ColdCache.MaxBytes = new FileInfo(Path.Combine(temp, keyB.Replace('/', Path.DirectorySeparatorChar))).Length;
            cache.Warm(keyB);
            var pathB = AwaitHit(cache, keyB);
            Assert.NotNull(pathB);
            Assert.Null(cache.TryGetLocalPath(keyA)); // A 已被 LRU 淘汰

            // Invalidate 删除缓存文件
            cache.Invalidate(keyB);
            Assert.False(File.Exists(pathB));
            Assert.Null(cache.TryGetLocalPath(keyB));

            await cache.StopAsync(CancellationToken.None);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不阻塞测试
            }
        }
    }

    /// <summary>轮询等待缓存命中（后台下载是异步队列消费）.</summary>
    private static string? AwaitHit(ColdCacheService cache, string objectKey)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var path = cache.TryGetLocalPath(objectKey);
            if (path != null)
            {
                return path;
            }

            Thread.Sleep(50);
        }

        return null;
    }

    private static void WriteColdObject(string root, string relativeKey, byte[] payload)
    {
        var path = Path.Combine(root, relativeKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, payload);
    }
}
