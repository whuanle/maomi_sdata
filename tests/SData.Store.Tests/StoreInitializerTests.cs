using System.Text.Json;
using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Infra.Services;
using SData.Settings;
using SData.Settings.Services;
using SData.Store.Core;

namespace SData.Store.Tests;

/// <summary>
/// 存储初始化 S3 SECRET 冷启动重建测试：设置页持久化值重启后必须重建运行时 SECRET——
/// 否则查询侧按 GetS3() 拼了 s3:// URL 而 httpfs 无匹配 SECRET，请求走默认 AWS 端点报 NoSuchBucket.
/// </summary>
[Collection("content-root-serial")]
public class StoreInitializerTests
{
    /// <summary>设置页保存过 S3：StartAsync 后运行时 SECRET 已按持久化值重建.</summary>
    [Fact]
    public async Task Start_PersistedS3Override_RebuildsRuntimeSecret()
    {
        using var harness = new Harness();
        harness.Settings.SaveAsync(
            SettingKeys.S3,
            JsonSerializer.Serialize(
                new S3Options
                {
                    Endpoint = "http://minio.local:9000",
                    Bucket = "sdata-cold",
                    Prefix = "sdata/archive",
                    AccessKey = "ak",
                    SecretKey = "sk",
                },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None).GetAwaiter().GetResult();

        await harness.Initializer.StartAsync(CancellationToken.None);

        var names = await harness.Manager.QueryAsync(
            "SELECT name FROM duckdb_secrets() WHERE name = 'sdata_s3'",
            null,
            async reader =>
            {
                var list = new List<string>();
                while (await reader.ReadAsync())
                {
                    list.Add(reader.GetString(0));
                }

                return list;
            },
            CancellationToken.None);
        Assert.Single(names);
    }

    /// <summary>未配置 S3（文件与设置页均无）：不建 SECRET（零行为变化）.</summary>
    [Fact]
    public async Task Start_NoS3_NoSecretCreated()
    {
        using var harness = new Harness();

        await harness.Initializer.StartAsync(CancellationToken.None);

        var count = await harness.Manager.QueryAsync(
            "SELECT count(*) FROM duckdb_secrets()",
            null,
            async reader =>
            {
                await reader.ReadAsync();
                return reader.GetInt64(0);
            },
            CancellationToken.None);
        Assert.Equal(0, count);
    }

    /// <summary>测试夹具：Manager 不预初始化（由 StoreInitializer.StartAsync 负责打开）.</summary>
    private sealed class Harness : IDisposable
    {
        /// <summary>临时目录.</summary>
        public readonly string TempDirectory;

        /// <summary>系统配置.</summary>
        public readonly SystemOptions Options;

        /// <summary>连接管理器（未初始化）.</summary>
        public readonly DuckDbConnectionManager Manager;

        /// <summary>运行时系统设置.</summary>
        public readonly SystemSettingsProvider Settings;

        /// <summary>存储初始化服务.</summary>
        public readonly StoreInitializer Initializer;

        /// <summary>
        /// Initializes a new instance of the <see cref="Harness"/> class.
        /// </summary>
        public Harness()
        {
            TempDirectory = Path.Combine(Path.GetTempPath(), "sdata-storeinit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDirectory);
            AppConst.ContentRoot = TempDirectory;

            Options = new SystemOptions();
            Options.DuckDb.Path = Path.Combine(TempDirectory, "storeinit-test.duckdb");
            Options.DuckDb.Threads = 2;

            Manager = new DuckDbConnectionManager(Options, NullLogger<DuckDbConnectionManager>.Instance);

            var sqlitePath = Path.Combine(TempDirectory, "settings.db");
            var dbOptions = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={sqlitePath}").Options;
            var ledger = new DatabaseContext(dbOptions);
            ledger.Database.EnsureCreated();
            var scopeFactory = new SingleScopeFactory(ledger);
            Settings = new SystemSettingsProvider(Options, scopeFactory, new DefaultIdProvider(1), NullLogger<SystemSettingsProvider>.Instance);

            Initializer = new StoreInitializer(
                Manager,
                new DuckDbSchemaBootstrapper(Manager, NullLogger<DuckDbSchemaBootstrapper>.Instance),
                Options,
                Settings,
                new S3SecretManager(Manager, NullLogger<S3SecretManager>.Instance));
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

    /// <summary>单例 scope 工厂（设置上下文直供，与 TableResolverTests 同构）.</summary>
    private sealed class SingleScopeFactory : IServiceScopeFactory
    {
        private readonly DatabaseContext _dbContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="SingleScopeFactory"/> class.
        /// </summary>
        /// <param name="dbContext">上下文.</param>
        public SingleScopeFactory(DatabaseContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <inheritdoc/>
        public IServiceScope CreateScope()
        {
            return new SingleScope(_dbContext);
        }

        private sealed class SingleScope : IServiceScope
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="SingleScope"/> class.
            /// </summary>
            /// <param name="dbContext">上下文.</param>
            public SingleScope(DatabaseContext dbContext)
            {
                ServiceProvider = new SingleProvider(dbContext);
            }

            /// <inheritdoc/>
            public IServiceProvider ServiceProvider { get; }

            /// <inheritdoc/>
            public void Dispose()
            {
                // 单例上下文由夹具统一释放
            }
        }

        private sealed class SingleProvider : IServiceProvider
        {
            private readonly DatabaseContext _dbContext;

            /// <summary>
            /// Initializes a new instance of the <see cref="SingleProvider"/> class.
            /// </summary>
            /// <param name="dbContext">上下文.</param>
            public SingleProvider(DatabaseContext dbContext)
            {
                _dbContext = dbContext;
            }

            /// <inheritdoc/>
            public object? GetService(Type serviceType)
            {
                return serviceType == typeof(DatabaseContext) ? _dbContext : null;
            }
        }
    }
}
