using Microsoft.Extensions.Logging;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Infra.Exceptions;
using SData.Settings;

namespace SData.Store.Core;

/// <summary>
/// S3 SECRET 管理（IS3SecretManager 实现，契约在 Settings.Shared）：
/// 应用 S3 配置重建 SECRET（设置页保存后立即生效）与连接测试（临时 SECRET + 写测试对象）.
/// </summary>
public class S3SecretManager : IS3SecretManager
{
    /// <summary>连接测试对象名（写入桶根/前缀下，数百字节，保留不清理）.</summary>
    public const string TestObjectName = ".sdata-conn-test.parquet";

    private readonly DuckDbConnectionManager _connectionManager;
    private readonly ILogger<S3SecretManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="S3SecretManager"/> class.
    /// </summary>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="logger">日志.</param>
    public S3SecretManager(DuckDbConnectionManager connectionManager, ILogger<S3SecretManager> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task ApplyS3SecretAsync(S3Options s3, CancellationToken cancellationToken)
    {
        return _connectionManager.ApplyS3SecretAsync(s3, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task TestConnectionAsync(S3Options s3, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(s3.Endpoint) || string.IsNullOrWhiteSpace(s3.Bucket))
        {
            throw BusinessException.Throw(400, "端点与存储桶不能为空");
        }

        if (S3SecretBuilder.NormalizeEndpoint(s3.Endpoint).Length == 0)
        {
            throw BusinessException.Throw(400, "S3 端点格式无效：请填写 host[:port]（可带 http:// 或 https:// 前缀，如 http://s3.example.com:9000）");
        }

        var bucket = s3.Bucket.Replace("'", "''");
        var prefix = string.IsNullOrWhiteSpace(s3.Prefix) ? string.Empty : s3.Prefix.TrimEnd('/') + "/";
        var target = $"s3://{bucket}/{prefix}{TestObjectName}".Replace("'", "''");

        // 独立临时 SECRET，不覆盖运行中的 sdata_s3；测试失败不影响当前冷层
        // ENDPOINT 裸 host[:port] + USE_SSL 按协议头推导 + path-style（IP 端点虚主机寻址无法解析）
        await _connectionManager.ExecuteWriteAsync(
            S3SecretBuilder.BuildCreateSql("sdata_s3_test", s3),
            cancellationToken: cancellationToken);
        try
        {
            await _connectionManager.ExecuteWriteAsync(
                $"COPY (SELECT 1 AS ok) TO '{target}' (FORMAT PARQUET)",
                cancellationToken: cancellationToken);
            _logger.LogInformation("S3 connection test succeeded (bucket={Bucket}, endpoint={Endpoint})", s3.Bucket, s3.Endpoint);
        }
        finally
        {
            try
            {
                await _connectionManager.ExecuteWriteAsync("DROP SECRET IF EXISTS sdata_s3_test", cancellationToken: cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "清理测试 SECRET 失败（不影响功能）");
            }
        }
    }
}
