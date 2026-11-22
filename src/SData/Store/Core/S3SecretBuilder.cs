using SData.Infra.Configuration;

namespace SData.Store.Core;

/// <summary>
/// S3 SECRET SQL 公共构件：DuckDB httpfs 的 ENDPOINT 只接受裸 host[:port]（协议由 USE_SSL 决定），
/// 带 http(s):// 前缀会导致请求 URL 拼接成 http://{bucket}.http//host... → Could not resolve hostname；
/// 自托管 S3 兼容存储（MinIO）多为 IP 端点，默认虚主机寻址（bucket.端点 当域名解析）永远不通，
/// 统一走 path-style（与 S3ObjectStore 的 ForcePathStyle 一致）。启动 SECRET（DuckDbConnectionManager）
/// 与连接测试（S3SecretManager）共用，保证两通道行为一致.
/// </summary>
internal static class S3SecretBuilder
{
    /// <summary>推导 USE_SSL：https 前缀 = true，其余（http:// 或无前缀）= false.</summary>
    /// <param name="endpoint">用户输入端点（可带协议头）.</param>
    /// <returns>true 用 https.</returns>
    public static bool UseSsl(string endpoint)
    {
        return endpoint.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 归一化端点为裸 host[:port]：剥离 http(s):// 前缀、首尾空白与结尾斜杠；仅协议头时返回空串.
    /// </summary>
    /// <param name="endpoint">用户输入端点.</param>
    /// <returns>裸 host[:port]，形如 minio:9000.</returns>
    public static string NormalizeEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["http://".Length..];
        }
        else if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["https://".Length..];
        }

        return trimmed.TrimEnd('/');
    }

    /// <summary>
    /// 组装 CREATE OR REPLACE SECRET SQL（单引号转义在此统一处理；
    /// secretName 为调用方内部常量，禁止传入用户输入）.
    /// </summary>
    /// <param name="secretName">SECRET 名称（内部常量）.</param>
    /// <param name="s3">S3 配置.</param>
    /// <returns>可执行的 CREATE OR REPLACE SECRET 语句.</returns>
    public static string BuildCreateSql(string secretName, S3Options s3)
    {
        var endpoint = NormalizeEndpoint(s3.Endpoint).Replace("'", "''");
        var accessKey = s3.AccessKey.Replace("'", "''");
        var secretKey = s3.SecretKey.Replace("'", "''");
        var bucket = s3.Bucket.Replace("'", "''");
        var useSsl = UseSsl(s3.Endpoint);
        return $"CREATE OR REPLACE SECRET {secretName} (TYPE S3, KEY_ID '{accessKey}', SECRET '{secretKey}', ENDPOINT '{endpoint}', USE_SSL {useSsl}, URL_STYLE 'path', SCOPE 's3://{bucket}')";
    }
}
