using Amazon.S3;
using Amazon.S3.Model;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Infra.Exceptions;
using Microsoft.Extensions.Logging;

namespace SData.Store.Core;

/// <summary>
/// 冷存档对象统一访问接口：本地冷存目录与 S3 对象同一抽象（删除/下载/存在性检查），
/// 供冷缓存预热（Wave 下载）与保留策略过期清理（删除）复用；单测走本地实现，S3 实现留 e2e 集成窗.
/// </summary>
public interface IObjectStore
{
    /// <summary>
    /// 对象是否存在（不存在/无权限返回 false，不抛异常）.
    /// </summary>
    /// <param name="objectKey">对象键（s3://bucket/key 或相对 ContentRoot 的本地路径）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>存在返回 true.</returns>
    Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载对象到本地文件（调用方用临时文件名传入，实现内做原子落位保证半写不可见）.
    /// </summary>
    /// <param name="objectKey">对象键.</param>
    /// <param name="localPath">目标本地文件路径（临时文件）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    Task DownloadAsync(string objectKey, string localPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除对象（S3 协议删除幂等：键不存在同样成功；本地实现忽略不存在的文件）.
    /// </summary>
    /// <param name="objectKey">对象键.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// 按 objectKey 前缀路由的复合对象存储：s3:// → S3，否则本地冷存（相对 ContentRoot）.
/// </summary>
public sealed class CompositeObjectStore : IObjectStore
{
    private readonly LocalObjectStore _local;
    private readonly S3ObjectStore _s3;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompositeObjectStore"/> class.
    /// </summary>
    /// <param name="local">本地对象存储.</param>
    /// <param name="s3">S3 对象存储.</param>
    public CompositeObjectStore(LocalObjectStore local, S3ObjectStore s3)
    {
        _local = local;
        _s3 = s3;
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        return IsS3Key(objectKey)
            ? _s3.ExistsAsync(objectKey, cancellationToken)
            : _local.ExistsAsync(objectKey, cancellationToken);
    }

    /// <inheritdoc/>
    public Task DownloadAsync(string objectKey, string localPath, CancellationToken cancellationToken = default)
    {
        return IsS3Key(objectKey)
            ? _s3.DownloadAsync(objectKey, localPath, cancellationToken)
            : _local.DownloadAsync(objectKey, localPath, cancellationToken);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        return IsS3Key(objectKey)
            ? _s3.DeleteAsync(objectKey, cancellationToken)
            : _local.DeleteAsync(objectKey, cancellationToken);
    }

    private static bool IsS3Key(string objectKey)
    {
        return objectKey.StartsWith("s3://", StringComparison.Ordinal);
    }
}

/// <summary>
/// 本地冷存对象存储（对象键为相对 ContentRoot 的路径，如 data/cold/logs/20260101.parquet）.
/// </summary>
public sealed class LocalObjectStore : IObjectStore
{
    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(Path.Combine(AppConst.GetPath(), objectKey)));
    }

    /// <inheritdoc/>
    public async Task DownloadAsync(string objectKey, string localPath, CancellationToken cancellationToken = default)
    {
        var source = Path.Combine(AppConst.GetPath(), objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await using var input = File.OpenRead(source);
        await using var output = File.Create(localPath);
        await input.CopyToAsync(output, cancellationToken);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(AppConst.GetPath(), objectKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// S3 兼容对象存储（AWSSDK.S3，ForcePathStyle 兼容 MinIO；Endpoint/Bucket 未配置时方法返回空结果不建连接）.
/// </summary>
public sealed class S3ObjectStore
{
    private readonly SystemOptions _options;
    private readonly ILogger<S3ObjectStore> _logger;
    private readonly Lazy<IAmazonS3> _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="S3ObjectStore"/> class.
    /// </summary>
    /// <param name="options">系统配置（S3 段）.</param>
    /// <param name="logger">日志.</param>
    public S3ObjectStore(SystemOptions options, ILogger<S3ObjectStore> logger)
    {
        _options = options;
        _logger = logger;
        _client = new Lazy<IAmazonS3>(CreateClient);
    }

    /// <summary>
    /// 对象是否存在.
    /// </summary>
    /// <param name="objectKey">s3:// 对象键.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>存在返回 true.</returns>
    public async Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
        {
            return false;
        }

        var (bucket, key) = ParseKey(objectKey);
        try
        {
            await _client.Value.GetObjectMetadataAsync(bucket, key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// 下载对象到本地临时文件（流式写盘，调用方负责原子落位）.
    /// </summary>
    /// <param name="objectKey">s3:// 对象键.</param>
    /// <param name="localPath">目标本地文件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    public async Task DownloadAsync(string objectKey, string localPath, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
        {
            throw BusinessException.Throw(400, "S3 冷存储未配置（Endpoint/Bucket 为空），无法下载冷数据");
        }

        var (bucket, key) = ParseKey(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        using var response = await _client.Value.GetObjectAsync(bucket, key, cancellationToken);
        await using var output = File.Create(localPath);
        await response.ResponseStream.CopyToAsync(output, cancellationToken);
    }

    /// <summary>
    /// 删除对象（S3 删除幂等，键不存在同样返回成功）.
    /// </summary>
    /// <param name="objectKey">s3:// 对象键.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
        {
            _logger.LogWarning("S3 冷存储未配置，跳过对象删除：{ObjectKey}", objectKey);
            return;
        }

        var (bucket, key) = ParseKey(objectKey);
        await _client.Value.DeleteObjectAsync(bucket, key, cancellationToken);
        _logger.LogInformation("Deleted cold object {ObjectKey}", objectKey);
    }

    private IAmazonS3 CreateClient()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = _options.S3.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        };
        return new AmazonS3Client(_options.S3.AccessKey, _options.S3.SecretKey, config);
    }

    private bool IsConfigured()
    {
        return !string.IsNullOrWhiteSpace(_options.S3.Endpoint) && !string.IsNullOrWhiteSpace(_options.S3.Bucket);
    }

    private static (string Bucket, string Key) ParseKey(string objectKey)
    {
        var trimmed = objectKey["s3://".Length..];
        var separator = trimmed.IndexOf('/');
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            throw BusinessException.Throw(400, $"非法 S3 对象键：{objectKey}");
        }

        return (trimmed[..separator], trimmed[(separator + 1)..]);
    }
}
