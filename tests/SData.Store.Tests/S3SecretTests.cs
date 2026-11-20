using global::Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Store.Core;

namespace SData.Store.Tests;

/// <summary>
/// S3 SECRET 连接初始化测试（T511，契约 C4）：未配置零行为变化、已配置离线建 SECRET、httpfs 可用性结论、
/// 端点归一化（剥协议头）与 path-style SECRET SQL（带协议头/IP 端点会导致 Could not resolve hostname 的修复回归）.
/// </summary>
public class S3SecretTests
{
    /// <summary>未配置 S3：初始化成功且无 SECRET（零行为变化）.</summary>
    [Fact]
    public async Task Initialize_WithoutS3_NoSecret()
    {
        using var harness = new S3Harness();
        var secrets = await harness.ListSecretsAsync();
        Assert.Empty(secrets);
    }

    /// <summary>已配置 S3（假端点，CREATE SECRET 不联网）：初始化成功且 SECRET 就位.</summary>
    [Fact]
    public async Task Initialize_WithS3_CreatesSecretOffline()
    {
        using var harness = new S3Harness(options =>
        {
            options.S3.Endpoint = "http://minio.local:9000";
            options.S3.Bucket = "sdata-cold";
            options.S3.Prefix = "sdata/archive";
            options.S3.AccessKey = "minioroot";
            options.S3.SecretKey = "mini'pass"; // 含单引号验证转义
        });

        var secrets = await harness.ListSecretsAsync();
        var secret = Assert.Single(secrets);
        Assert.Contains("sdata_s3", secret, StringComparison.Ordinal);
    }

    /// <summary>httpfs 扩展在 DuckDB.NET.Data.Full 内静态可用（installed），T512 read_parquet 冷层读的前提.</summary>
    [Fact]
    public void Httpfs_IsInstalledInFullBuild()
    {
        using var harness = new S3Harness();
        var installed = harness.Manager.QueryAsync(
            "SELECT installed, loaded FROM duckdb_extensions() WHERE extension_name = 'httpfs'",
            null,
            async reader =>
            {
                await reader.ReadAsync();
                return (Installed: reader.GetBoolean(0), Loaded: reader.GetBoolean(1));
            }).GetAwaiter().GetResult();
        Assert.True(installed.Installed, "httpfs 应在 Full 构建中静态内置");
    }

    /// <summary>裸端点（无协议头，如 minio.local:9000）同样离线建 SECRET（USE_SSL=false 推导路径）.</summary>
    [Fact]
    public async Task Initialize_WithBareEndpoint_CreatesSecretOffline()
    {
        using var harness = new S3Harness(options =>
        {
            options.S3.Endpoint = "minio.local:9000";
            options.S3.Bucket = "sdata-cold";
            options.S3.AccessKey = "minioroot";
            options.S3.SecretKey = "minipass";
        });

        var secrets = await harness.ListSecretsAsync();
        Assert.Contains("sdata_s3", secrets);
    }

    /// <summary>仅协议头端点（http://）视为未配置：不建 SECRET，冷层保持关闭.</summary>
    [Fact]
    public async Task Initialize_WithSchemeOnlyEndpoint_SkipsSecret()
    {
        using var harness = new S3Harness(options =>
        {
            options.S3.Endpoint = "http://";
            options.S3.Bucket = "sdata-cold";
        });

        var secrets = await harness.ListSecretsAsync();
        Assert.Empty(secrets);
    }

    /// <summary>端点归一化：剥离 http(s):// 前缀/结尾斜杠/首尾空白，仅协议头返回空串.</summary>
    [Theory]
    [InlineData("http://192.0.2.10:9000", "192.0.2.10:9000")]
    [InlineData("https://minio.local:9000/", "minio.local:9000")]
    [InlineData("  minio:9000  ", "minio:9000")]
    [InlineData("http://", "")]
    public void NormalizeEndpoint_StripsSchemeAndTrailingSlash(string input, string expected)
    {
        Assert.Equal(expected, S3SecretBuilder.NormalizeEndpoint(input));
    }

    /// <summary>USE_SSL 推导：https = true，http:// 与无前缀 = false.</summary>
    [Theory]
    [InlineData("https://minio:9000", true)]
    [InlineData("http://minio:9000", false)]
    [InlineData("minio:9000", false)]
    public void UseSsl_DerivedFromScheme(string input, bool expected)
    {
        Assert.Equal(expected, S3SecretBuilder.UseSsl(input));
    }

    /// <summary>SECRET SQL：ENDPOINT 为裸 host:port、URL_STYLE 'path'（IP 端点虚主机寻址无法解析）与单引号转义.</summary>
    [Fact]
    public void BuildCreateSql_UsesBareEndpointAndPathStyle()
    {
        var s3 = new S3Options
        {
            Endpoint = "http://192.0.2.10:9000",
            Bucket = "sd'ata",
            AccessKey = "ak'1",
            SecretKey = "sk'2",
        };
        var sql = S3SecretBuilder.BuildCreateSql("sdata_s3", s3);
        Assert.Contains("ENDPOINT '192.0.2.10:9000'", sql, StringComparison.Ordinal);
        Assert.Contains("URL_STYLE 'path'", sql, StringComparison.Ordinal);
        Assert.Contains("USE_SSL False", sql, StringComparison.Ordinal);
        Assert.Contains("SCOPE 's3://sd''ata'", sql, StringComparison.Ordinal);
        Assert.Contains("KEY_ID 'ak''1'", sql, StringComparison.Ordinal);
    }

    /// <summary>S3 初始化测试夹具.</summary>
    private sealed class S3Harness : IDisposable
    {
        /// <summary>临时目录.</summary>
        public readonly string TempDirectory;

        /// <summary>连接管理器.</summary>
        public readonly DuckDbConnectionManager Manager;

        /// <summary>取消令牌.</summary>
        public readonly CancellationToken Token = CancellationToken.None;

        /// <summary>
        /// Initializes a new instance of the <see cref="S3Harness"/> class.
        /// </summary>
        /// <param name="configure">配置回调.</param>
        public S3Harness(Action<SystemOptions>? configure = null)
        {
            TempDirectory = Path.Combine(Path.GetTempPath(), "sdata-s3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDirectory);

            var options = new SystemOptions();
            options.DuckDb.Path = Path.Combine(TempDirectory, "s3-test.duckdb");
            options.DuckDb.Threads = 2;
            configure?.Invoke(options);

            Manager = new DuckDbConnectionManager(options, NullLogger<DuckDbConnectionManager>.Instance);
            Manager.InitializeAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// 列出 duckdb_secrets() 名称.
        /// </summary>
        /// <returns>secret 名称列表.</returns>
        public async Task<List<string>> ListSecretsAsync()
        {
            return await Manager.QueryAsync(
                "SELECT name FROM duckdb_secrets()",
                null,
                async reader =>
                {
                    var list = new List<string>();
                    while (await reader.ReadAsync(Token))
                    {
                        list.Add(reader.GetString(0));
                    }

                    return list;
                }, Token);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不阻塞测试
            }
        }
    }
}
