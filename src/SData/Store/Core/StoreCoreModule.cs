using Maomi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Settings;

namespace SData.Store;

/// <summary>
/// 存储模块：DuckDB 连接管理、Schema 引导、接入缓冲与批量落库、保留策略、各查询存储.
/// </summary>
[InjectModule<SDataStoreSharedModule>]
public class StoreCoreModule : IModule
{
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="StoreCoreModule"/> class.
    /// </summary>
    /// <param name="configuration">配置.</param>
    public StoreCoreModule(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddSingleton<DuckDbConnectionManager>();
        context.Services.AddSingleton<IDuckDbRuntimeSettings>(sp => sp.GetRequiredService<DuckDbConnectionManager>());
        context.Services.AddSingleton<DuckDbSchemaBootstrapper>();
        context.Services.AddSingleton<IngestBuffer>();
        context.Services.AddSingleton<SData.Store.IIngestBuffer>(sp => sp.GetRequiredService<IngestBuffer>());
        context.Services.AddSingleton<SData.Store.IAnalyticsExecutor, AnalyticsExecutor>();
        context.Services.AddSingleton<IAnalyticsCatalog, AnalyticsCatalog>();
        context.Services.AddSingleton<SData.Store.IStoreInfo>(sp => sp.GetRequiredService<DuckDbConnectionManager>());
        context.Services.AddSingleton<SData.Store.IStorePerformanceMetrics>(sp => sp.GetRequiredService<DuckDbConnectionManager>());
        context.Services.AddScoped<SData.Store.ILogStore, LogStore>();
        context.Services.AddScoped<SData.Store.ITraceStore, TraceStore>();
        context.Services.AddScoped<SData.Store.IMetricStore, MetricStore>();
        context.Services.AddScoped<SData.Store.ITelemetryStore, TelemetryStore>();
        context.Services.AddSingleton<SData.Store.IRumStore, RumStore>();
        context.Services.AddSingleton<SData.Store.Models.IMqConsumptionStore, MqConsumptionStore>();
        context.Services.AddSingleton<SData.Settings.IS3SecretManager, S3SecretManager>();
        context.Services.AddSingleton<ColdLedgerCache>();
        context.Services.AddSingleton<LocalObjectStore>();
        context.Services.AddSingleton<S3ObjectStore>();
        context.Services.AddSingleton<SData.Store.Core.IObjectStore, CompositeObjectStore>();
        context.Services.AddSingleton<ColdCacheService>();
        context.Services.AddHostedService(sp => sp.GetRequiredService<ColdCacheService>());
        context.Services.AddSingleton<SData.Store.QueryDsl.ITableResolver, TableResolver>();
        context.Services.AddSingleton<QueryDslExecutorService>();
        // 查询执行器包装短 TTL 结果缓存装饰器（QueryCacheTtlSeconds=0 时直通）
        context.Services.AddSingleton<SData.Store.QueryDsl.IQueryDslExecutor>(sp =>
            new CachedQueryDslExecutor(sp.GetRequiredService<QueryDslExecutorService>(), sp.GetRequiredService<SystemOptions>()));
        context.Services.AddHostedService<StoreInitializer>();
        context.Services.AddHostedService<IngestWriterService>();
        context.Services.AddHostedService<RetentionService>();
        context.Services.AddHostedService<RollupService>();
        context.Services.AddHostedService<ArchiveService>();
        context.Services.AddSingleton<StorageMetricsService>();
        context.Services.AddSingleton<IStorageMetrics>(sp => sp.GetRequiredService<StorageMetricsService>());
        context.Services.AddHostedService(sp => sp.GetRequiredService<StorageMetricsService>());
    }
}

/// <summary>
/// 存储初始化：DuckDB 打开 + Schema 引导 + 设置页持久化 S3 SECRET 重建.
/// </summary>
public class StoreInitializer : IHostedService
{
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly DuckDbSchemaBootstrapper _bootstrapper;
    private readonly SystemOptions _systemOptions;
    private readonly ISystemSettingsProvider _settingsProvider;
    private readonly IS3SecretManager _s3SecretManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="StoreInitializer"/> class.
    /// </summary>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="bootstrapper">Schema 引导.</param>
    /// <param name="systemOptions">系统配置（文件回退默认）.</param>
    /// <param name="settingsProvider">运行时系统设置（SQLite 覆盖优先）.</param>
    /// <param name="s3SecretManager">S3 SECRET 管理.</param>
    public StoreInitializer(DuckDbConnectionManager connectionManager, DuckDbSchemaBootstrapper bootstrapper, SystemOptions systemOptions, ISystemSettingsProvider settingsProvider, IS3SecretManager s3SecretManager)
    {
        _connectionManager = connectionManager;
        _bootstrapper = bootstrapper;
        _systemOptions = systemOptions;
        _settingsProvider = settingsProvider;
        _s3SecretManager = s3SecretManager;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var runtime = _settingsProvider.GetRuntime().DuckDb;
        await _connectionManager.InitializeAsync(cancellationToken, _settingsProvider.GetDuckDb().MemoryLimit, runtime.Threads, runtime.MaxConcurrentQueries);
        await _bootstrapper.ApplyAsync(cancellationToken);

        // 冷启动重建设置页持久化的 S3 SECRET：DuckDB SECRET 是连接实例内存态，重启即失，
        // InitializeAsync 只按文件配置（SystemOptions.S3）建，设置页保存值（SQLite setting 表）不在其中；
        // 而查询侧 TableResolver 按 GetS3()（覆盖 > 文件）拼 s3:// URL——无匹配 SECRET 时 httpfs
        // 走默认 AWS 端点（报 in region '' + NoSuchBucket，请求根本没到自建 MinIO）。
        // 以 GetS3() 为唯一真相源重建 SECRET（CREATE OR REPLACE 幂等），并同步回 SystemOptions.S3，
        // 使 S3ObjectStore/ColdCacheService 等读 _options.S3 的通道（缓存预热/对象删除）同源生效.
        // 本服务晚于 DatabaseInitializer 启动，SQLite setting 表已就绪.
        var s3 = _settingsProvider.GetS3();
        _systemOptions.S3 = s3;
        await _s3SecretManager.ApplyS3SecretAsync(s3, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _connectionManager.DisposeAsync();
    }
}

/// <summary>
/// SDataStoreSharedModule.
/// </summary>
public class SDataStoreSharedModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
    }
}
