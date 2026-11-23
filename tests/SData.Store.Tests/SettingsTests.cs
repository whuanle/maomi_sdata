using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Infra.Configuration;
using SData.Infra.Services;
using SData.Settings;
using SData.Settings.Handlers;
using SData.Settings.Services;

namespace SData.Store.Tests;

/// <summary>
/// 系统设置测试：provider 合并语义（文件回退/覆盖生效/脏数据回退）、压缩白名单、S3 密钥掩码语义.
/// </summary>
public class SettingsTests
{
    /// <summary>无覆盖时回退文件配置.</summary>
    [Fact]
    public void Provider_NoOverride_FallsBackToFile()
    {
        using var harness = new SettingsHarness(options =>
        {
            options.S3.Endpoint = "http://file-minio:9000";
            options.S3.Bucket = "file-bucket";
            options.Retention.Logs.HotDays = 7;
            options.Compression.ArchiveParquetCodec = "gzip";
        });

        var s3 = harness.Provider.GetS3();
        Assert.Equal("http://file-minio:9000", s3.Endpoint);
        Assert.Equal("file-bucket", s3.Bucket);
        Assert.Equal(7, harness.Provider.GetRetention().Logs.HotDays);
        Assert.Equal("gzip", harness.Provider.GetCompression().ArchiveParquetCodec);
    }

    /// <summary>保存覆盖后立即生效（缓存刷新，S3/保留/压缩立即生效的基础）.</summary>
    [Fact]
    public async Task Provider_SaveOverride_TakesEffect()
    {
        using var harness = new SettingsHarness();
        var retention = new RetentionPlanOptions();
        retention.Logs.HotDays = 3;
        retention.Logs.ColdDays = 45;
        await harness.Provider.SaveAsync(SettingKeys.Retention, System.Text.Json.JsonSerializer.Serialize(retention), harness.Token);
        Assert.Equal(3, harness.Provider.GetRetention().Logs.HotDays);
        Assert.Equal(45, harness.Provider.GetRetention().Logs.ColdDays);
        Assert.Equal(14, harness.Provider.GetRetention().Traces.HotDays); // 未覆盖的域回退文件默认

        await harness.Provider.SaveAsync(SettingKeys.Compression, System.Text.Json.JsonSerializer.Serialize(new CompressionOptions { ArchiveParquetCodec = "zstd" }), harness.Token);
        Assert.Equal("zstd", harness.Provider.GetCompression().ArchiveParquetCodec);
    }

    /// <summary>页面命令包装保存一天归档策略，冷存天数与未启用域保持不变.</summary>
    [Fact]
    public async Task SaveRetention_OneDay_RoundTripsCommand()
    {
        using var harness = new SettingsHarness();
        var command = System.Text.Json.JsonSerializer.Deserialize<SaveRetentionSettingsCommand>(
            """{"retention":{"logs":{"hotDays":1,"coldDays":90},"traces":{"hotDays":1,"coldDays":60},"metrics":{"hotDays":1,"coldDays":180},"iot":{"hotDays":365,"coldDays":0},"rum":{"hotDays":30,"coldDays":90}}}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var handler = new SaveRetentionSettingsCommandHandler(harness.Provider);
        await handler.Handle(command, harness.Token);

        var saved = harness.Provider.GetRetention();
        Assert.Equal(1, saved.Logs.HotDays);
        Assert.Equal(90, saved.Logs.ColdDays);
        Assert.Equal(1, saved.Traces.HotDays);
        Assert.Equal(1, saved.Metrics.HotDays);
        Assert.Equal(0, saved.Iot.ColdDays);
        Assert.Equal(30, saved.Rum.HotDays);
    }

    /// <summary>脏数据（非法 JSON）不阻塞业务，回退文件默认.</summary>
    [Fact]
    public async Task Provider_CorruptJson_FallsBackToFile()
    {
        using var harness = new SettingsHarness(options => options.Retention.Logs.HotDays = 21);
        await harness.Provider.SaveAsync(SettingKeys.Retention, "{not-valid-json", harness.Token);
        Assert.Equal(21, harness.Provider.GetRetention().Logs.HotDays);
    }

    /// <summary>压缩编码不在白名单时回退 snappy（防脏数据进入 COPY 语句）.</summary>
    [Fact]
    public async Task Provider_InvalidCodec_FallsBackToSnappy()
    {
        using var harness = new SettingsHarness();
        await harness.Provider.SaveAsync(SettingKeys.Compression, System.Text.Json.JsonSerializer.Serialize(new CompressionOptions { ArchiveParquetCodec = "brotli" }), harness.Token);
        Assert.Equal("snappy", harness.Provider.GetCompression().ArchiveParquetCodec);
    }

    /// <summary>保存 S3 时 SecretKey 传掩码/空 = 沿用已存密钥，且立即应用 SECRET.</summary>
    [Fact]
    public async Task SaveS3_MaskedSecret_KeepsStoredSecret()
    {
        using var harness = new SettingsHarness();
        var storedSecret = "ts-" + Guid.NewGuid().ToString("N"); // 运行时生成的假密钥（非真实凭据）
        var first = new S3Options
        {
            Endpoint = "http://minio:9000",
            Bucket = "sdata-cold",
            Prefix = "sdata/archive",
            ArchiveGranularity = "hour",
            AccessKey = "minioroot",
            SecretKey = storedSecret,
        };
        await harness.Provider.SaveAsync(SettingKeys.S3, System.Text.Json.JsonSerializer.Serialize(first), harness.Token);

        var secretManager = new FakeSecretManager();
        var handler = new SaveS3SettingsCommandHandler(harness.Provider, secretManager);
        var model = await handler.Handle(new SaveS3SettingsCommand
        {
            Endpoint = "http://minio2:9000",
            Bucket = "sdata-cold2",
            SecretKey = S3SettingsModel.SecretMask, // 掩码 = 保留已存密钥
            AccessKey = "minioroot",
        }, harness.Token);

        Assert.Equal("hour", harness.Provider.GetS3().ArchiveGranularity);
        Assert.Equal(storedSecret, harness.Provider.GetS3().SecretKey);
        Assert.Equal("http://minio2:9000", harness.Provider.GetS3().Endpoint);
        Assert.Equal(S3SettingsModel.SecretMask, model.SecretKey); // 响应始终掩码
        Assert.True(model.HasSecret);
        Assert.NotNull(secretManager.LastApplied); // 保存后立即应用 SECRET（立即生效）
    }

    /// <summary>启用冷存储但缺密钥时 400.</summary>
    [Fact]
    public async Task SaveS3_MissingSecret_WhenEnabled_BadRequest()
    {
        using var harness = new SettingsHarness();
        var handler = new SaveS3SettingsCommandHandler(harness.Provider, new FakeSecretManager());
        await Assert.ThrowsAsync<Infra.Exceptions.BusinessException>(async () => await handler.Handle(new SaveS3SettingsCommand
        {
            Endpoint = "http://minio:9000",
            Bucket = "sdata-cold",
            AccessKey = "minioroot",
            SecretKey = string.Empty,
        }, harness.Token));
    }

    [Fact]
    public async Task MqttAuth_SaveHashesAndKeepOnlySameMode()
    {
        using var harness = new SettingsHarness();
        Assert.Equal("none", harness.Provider.GetMqttAuth().Mode);
        var hasher = new Pbkdf2PasswordHasher();
        var handler = new SaveMqttAuthSettingsCommandHandler(harness.Provider, hasher);
        var key = "test-" + Guid.NewGuid().ToString("N");
        var result = await handler.Handle(new() { Mode = "shared", Key = key }, default);
        Assert.True(result.HasKey);
        var saved = harness.Provider.GetMqttAuth();
        Assert.True(hasher.Verify(key, saved.KeyHash));
        var restarted = new SystemSettingsProvider(new SystemOptions(), new SettingsHarness.SingleScopeFactory(harness.Db), new DefaultIdProvider(3), NullLogger<SystemSettingsProvider>.Instance);
        Assert.Equal(saved.KeyHash, restarted.GetMqttAuth().KeyHash);
        Assert.DoesNotContain(key, (await harness.Db.Settings.SingleAsync(x => x.Key == SettingKeys.MqttAuth)).Value);
        await handler.Handle(new() { Mode = "shared", Key = "" }, default);
        Assert.Equal(saved.KeyHash, harness.Provider.GetMqttAuth().KeyHash);
        await Assert.ThrowsAsync<Infra.Exceptions.BusinessException>(async () => await handler.Handle(new() { Mode = "prefix" }, default));
        await Assert.ThrowsAsync<Infra.Exceptions.BusinessException>(async () => await handler.Handle(new() { Mode = "prefix", Key = "   " }, default));
        await handler.Handle(new() { Mode = "prefix", Key = key }, default);
        Assert.Equal(key.Length, harness.Provider.GetMqttAuth().PrefixLength);
        var response = await new QueryMqttAuthSettingsCommandHandler(harness.Provider).Handle(new(), default);
        Assert.DoesNotContain(key, System.Text.Json.JsonSerializer.Serialize(response));
        await handler.Handle(new() { Mode = "none" }, default);
        Assert.Empty(harness.Provider.GetMqttAuth().KeyHash);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"mode\":\"prefix\",\"prefixLength\":0}")]
    public async Task MqttAuth_CorruptOverrideNeverFallsBackToNone(string json)
    {
        using var harness = new SettingsHarness();
        await harness.Provider.SaveAsync(SettingKeys.MqttAuth, json, default);
        Assert.False(harness.Provider.GetMqttAuth().IsValid);
    }

    [Fact]
    public async Task OtlpHeaders_PreserveReplaceClearAndRedact()
    {
        using var harness = new SettingsHarness(options =>
        {
            options.OTLP.TraceHeaders = "authorization=Bearer file-trace";
            options.OTLP.MetricsHeaders = "x-api-key=file-metrics";
        });
        var handler = new SaveOtlpSettingsCommandHandler(harness.Provider);
        var first = await handler.Handle(new SaveOtlpSettingsCommand(), default);
        Assert.Equal(OtlpHeaders.SecretMask, first.TraceHeaders);
        Assert.Equal("authorization=Bearer file-trace", harness.Provider.GetOtlp().TraceHeaders);
        var response = await new QuerySettingsCommandHandler(harness.Provider, new SystemOptions(), new FakeRuntime(), new ConfigurationBuilder().Build()).Handle(new(), default);
        Assert.Equal(OtlpHeaders.SecretMask, response.Otlp.MetricsHeaders);
        Assert.Equal("x-api-key=file-metrics", harness.Provider.GetOtlp().MetricsHeaders);

        await handler.Handle(new() { TraceHeaders = "Authorization=Basic dGVzdA==", MetricsHeaders = OtlpHeaders.SecretMask }, default);
        Assert.Equal("authorization=Basic dGVzdA==", harness.Provider.GetOtlp().TraceHeaders);
        Assert.Equal("x-api-key=file-metrics", harness.Provider.GetOtlp().MetricsHeaders);
        await handler.Handle(new() { ClearTraceHeaders = true }, default);
        Assert.Empty(harness.Provider.GetOtlp().TraceHeaders);
        var restarted = new SystemSettingsProvider(new SystemOptions(), new SettingsHarness.SingleScopeFactory(harness.Db),
            new DefaultIdProvider(3), NullLogger<SystemSettingsProvider>.Instance);
        Assert.Empty(restarted.GetOtlp().TraceHeaders);
        Assert.Equal("x-api-key=file-metrics", restarted.GetOtlp().MetricsHeaders);
        var startup = SystemSettingsProvider.TryLoadOtlpOverride(Path.Combine(harness.TempDirectory, "settings.db"));
        Assert.NotNull(startup);
        Assert.Equal("x-api-key=file-metrics", startup.MetricsHeaders);
        await Assert.ThrowsAsync<Infra.Exceptions.BusinessException>(async () => await handler.Handle(
            new() { TraceHeaders = "authorization=secret", ClearTraceHeaders = true }, default));
    }

    [Fact]
    public async Task MqAlerts_ValidateEnabledChannelsAndPersistRetrySettings()
    {
        using var harness = new SettingsHarness();
        harness.Db.NotificationChannels.Add(new SData.Database.Entities.NotificationChannelEntity
        {
            Id = 1, Name = "fake", Type = "webhook", Enabled = true, ProtectedConfig = "unused",
        });
        await harness.Db.SaveChangesAsync();
        var handler = new SaveMqInstancesCommandHandler(harness.Provider, new DefaultIdProvider(2), harness.Db);
        var instance = new MqInstanceOptions
        {
            Name = "consumer", AlertsEnabled = true, NotificationChannelIds = ["1"],
            RetryInitialSeconds = 3, RetryMaxSeconds = 90, AlertCooldownMinutes = 15,
        };
        await handler.Handle(new SaveMqInstancesCommand { Instances = [instance] }, default);
        var saved = Assert.Single(harness.Provider.GetMqInstances());
        Assert.Equal(3, saved.RetryInitialSeconds);
        Assert.Equal(90, saved.RetryMaxSeconds);
        Assert.Equal(15, saved.AlertCooldownMinutes);
        Assert.Equal(["1"], saved.NotificationChannelIds);
        var restarted = new SystemSettingsProvider(new SystemOptions(), new SettingsHarness.SingleScopeFactory(harness.Db),
            new DefaultIdProvider(3), NullLogger<SystemSettingsProvider>.Instance);
        Assert.True(Assert.Single(restarted.GetMqInstances()).AlertsEnabled);
        var channel = await harness.Db.NotificationChannels.SingleAsync();
        channel.Enabled = false;
        await harness.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<Infra.Exceptions.BusinessException>(async () =>
            await handler.Handle(new SaveMqInstancesCommand { Instances = [instance] }, default));
        Assert.Equal(400, error.StatusCode);
    }

    [Theory]
    [InlineData(" 1.50 gb ", "1.5GB")]
    [InlineData("2048MiB", "2048MiB")]
    [InlineData("0.5GiB", "0.5GiB")]
    public void DuckDbCapacity_Normalizes(string input, string expected)
    {
        Assert.True(DuckDbMemoryLimit.TryNormalize(input, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0GB")]
    [InlineData("-1GB")]
    [InlineData("unlimited")]
    [InlineData("1TB")]
    [InlineData("1e3MB")]
    [InlineData("9999999999999999GB")]
    [InlineData("2GB'; DROP TABLE settings; --")]
    public void DuckDbCapacity_RejectsInvalidInput(string? input)
    {
        var validator = new FluentValidation.InlineValidator<SaveDuckDbSettingsCommand>();
        SaveDuckDbSettingsCommand.Validate(validator);
        Assert.False(validator.Validate(new SaveDuckDbSettingsCommand { MemoryLimit = input! }).IsValid);
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"memoryLimit\":\"unlimited\"}")]
    [InlineData("{\"memoryLimit\":null}")]
    public async Task DuckDbProvider_InvalidOverrideFallsBackToFile(string json)
    {
        using var harness = new SettingsHarness(options => options.DuckDb.MemoryLimit = "768MB");
        await harness.Provider.SaveAsync(SettingKeys.DuckDb, json, default);
        Assert.Equal("768MB", harness.Provider.GetDuckDb().MemoryLimit);
    }

    [Fact]
    public async Task DuckDbSettings_SaveAndReload_OnlyOverridesMemory()
    {
        using var harness = new SettingsHarness();
        var runtime = new FakeRuntime();
        var service = new DuckDbSettingsService(harness.Provider, runtime, NullLogger<DuckDbSettingsService>.Instance);
        var saved = await new SaveDuckDbSettingsCommandHandler(service).Handle(new() { MemoryLimit = " 2 gb " }, default);
        Assert.Equal("2GB", saved.MemoryLimit);
        Assert.Equal("2GB", runtime.MemoryLimit);
        var restarted = new SystemSettingsProvider(new SystemOptions(), new SettingsHarness.SingleScopeFactory(harness.Db),
            new DefaultIdProvider(3), NullLogger<SystemSettingsProvider>.Instance);
        Assert.Equal("2GB", restarted.GetDuckDb().MemoryLimit);
        var query = await new QuerySettingsCommandHandler(harness.Provider, new SystemOptions(), runtime, new ConfigurationBuilder().Build()).Handle(new(), default);
        Assert.Equal(saved.EffectiveMemoryLimit, query.DuckDb.EffectiveMemoryLimit);
        var json = (await harness.Db.Settings.SingleAsync(x => x.Key == SettingKeys.DuckDb)).Value;
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("threads", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuckDbSettings_RuntimeFailureDoesNotPersist()
    {
        using var harness = new SettingsHarness();
        var runtime = new FakeRuntime { FailValue = "2GB" };
        var service = new DuckDbSettingsService(harness.Provider, runtime, NullLogger<DuckDbSettingsService>.Instance);
        var error = await Assert.ThrowsAsync<Infra.Exceptions.BusinessException>(() => service.SaveAsync("2GB", default));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal("512MB", runtime.MemoryLimit);
        Assert.Empty(await harness.Db.Settings.ToListAsync());
    }

    [Fact]
    public async Task DuckDbSettings_PersistenceFailureRestoresRuntime()
    {
        using var harness = new SettingsHarness();
        var runtime = new FakeRuntime();
        harness.Provider.GetDuckDb();
        await harness.Db.Database.ExecuteSqlRawAsync("DROP TABLE setting");
        var service = new DuckDbSettingsService(harness.Provider, runtime, NullLogger<DuckDbSettingsService>.Instance);
        await Assert.ThrowsAsync<Infra.Exceptions.BusinessException>(() => service.SaveAsync("3GB", default));
        Assert.Equal("512MB", runtime.MemoryLimit); // 恢复到保存前的运行值（FakeRuntime 初始值）
        Assert.Equal("2GB", harness.Provider.GetDuckDb().MemoryLimit); // 无持久化覆盖时回退文件默认（现默认 2GB）
    }

    [Fact]
    public async Task DuckDbSettings_RollbackFailureIsExplicit()
    {
        using var harness = new SettingsHarness();
        var runtime = new FakeRuntime { FailValue = "512MB" };
        harness.Provider.GetDuckDb();
        await harness.Db.Database.ExecuteSqlRawAsync("DROP TABLE setting");
        var service = new DuckDbSettingsService(harness.Provider, runtime, NullLogger<DuckDbSettingsService>.Instance);
        var error = await Assert.ThrowsAsync<Infra.Exceptions.BusinessException>(() => service.SaveAsync("2GB", default));
        Assert.Equal(503, error.StatusCode);
        Assert.Contains("恢复失败", error.Message);
        Assert.Equal("2GB", runtime.MemoryLimit);
    }

    [Fact]
    public async Task DuckDbSettings_ConcurrentSavesStayConsistent()
    {
        using var harness = new SettingsHarness();
        var runtime = new FakeRuntime();
        var service = new DuckDbSettingsService(harness.Provider, runtime, NullLogger<DuckDbSettingsService>.Instance);
        await Task.WhenAll(Enumerable.Range(1, 8).Select(i => service.SaveAsync($"{i}GB", default)));
        Assert.Equal(runtime.MemoryLimit, harness.Provider.GetDuckDb().MemoryLimit);
    }

    private sealed class FakeRuntime : IDuckDbRuntimeSettings
    {
        public string MemoryLimit { get; private set; } = "512MB";
        public string EffectiveMemoryLimit => MemoryLimit;
        public string? FailValue { get; init; }

        public async Task ApplyMemoryLimitAsync(string memoryLimit, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (memoryLimit == FailValue) throw new InvalidOperationException("fake application failure");
            MemoryLimit = memoryLimit;
        }

        public int Threads { get; private set; } = 2;
        public int MaxConcurrentQueries { get; private set; } = 1;

        public Task ApplyThreadsAsync(int threads, CancellationToken cancellationToken)
        {
            Threads = threads;
            return Task.CompletedTask;
        }

        public void ApplyMaxConcurrentQueries(int maxConcurrentQueries) => MaxConcurrentQueries = maxConcurrentQueries;
    }

    /// <summary>S3 SECRET 管理假实现（记录调用）.</summary>
    private sealed class FakeSecretManager : IS3SecretManager
    {
        /// <summary>最近一次应用的 S3 配置.</summary>
        public S3Options? LastApplied { get; private set; }

        /// <inheritdoc/>
        public Task ApplyS3SecretAsync(S3Options s3, CancellationToken cancellationToken)
        {
            LastApplied = s3;
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task TestConnectionAsync(S3Options s3, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>设置测试夹具（SQLite Settings 表 + provider）.</summary>
    private sealed class SettingsHarness : IDisposable
    {
        /// <summary>临时目录.</summary>
        public readonly string TempDirectory;

        /// <summary>系统设置提供器.</summary>
        public readonly SystemSettingsProvider Provider;

        /// <summary>取消令牌.</summary>
        public readonly CancellationToken Token = CancellationToken.None;

        /// <summary>SQLite 上下文.</summary>
        public readonly DatabaseContext Db;

        /// <summary>
        /// Initializes a new instance of the <see cref="SettingsHarness"/> class.
        /// </summary>
        /// <param name="configure">文件配置回调.</param>
        public SettingsHarness(Action<SystemOptions>? configure = null)
        {
            TempDirectory = Path.Combine(Path.GetTempPath(), "sdata-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDirectory);

            var options = new SystemOptions();
            configure?.Invoke(options);

            var sqlitePath = Path.Combine(TempDirectory, "settings.db");
            var dbOptions = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={sqlitePath}").Options;
            Db = new DatabaseContext(dbOptions);
            Db.Database.EnsureCreated();

            var scopeFactory = new SingleScopeFactory(Db);
            Provider = new SystemSettingsProvider(options, scopeFactory, new DefaultIdProvider(2), NullLogger<SystemSettingsProvider>.Instance);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Db.Dispose();
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不阻塞测试
            }
        }

        /// <summary>单例 scope 工厂.</summary>
        public sealed class SingleScopeFactory : IServiceScopeFactory
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
}
