using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using SData.Database;
using SData.Infra.Services;

namespace SData.Ingest.Services;

/// <summary>
/// 接入 API Key 校验（SQLite 存储 + 30 秒内存缓存）.
/// </summary>
public class IngestKeyValidator : IIngestKeyValidator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 4096 });
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestKeyValidator"/> class.
    /// </summary>
    /// <param name="scopeFactory">scope 工厂（避免单例持有 scoped DbContext）.</param>
    public IngestKeyValidator(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// 校验 key.
    /// </summary>
    /// <param name="apiKey">明文 key.</param>
    /// <returns>是否有效.</returns>
    public async Task<bool> ValidateAsync(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length > 256)
        {
            return false;
        }

        var hash = HashHelper.Sha256Hex(apiKey);
        var version = ApiKeyEpoch.Version;
        var cacheKey = (version, hash);
        if (_cache.TryGetValue(cacheKey, out bool cachedValid))
        {
            return cachedValid;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var valid = await dbContext.IngestKeys
            .Where(x => x.Enabled && x.KeyHash == hash)
            .AnyAsync();

        _cache.Set(cacheKey, valid, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = _cacheDuration, Size = 1 });
        return valid;
    }
}
