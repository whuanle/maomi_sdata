using Maomi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SData.Database;
using SData.Infra;
using SData.Infra.Configuration;

namespace SData.Database.Sqlite;

/// <summary>
/// SQLite 元数据库模块：注册 DbContext、启动时 EnsureCreated + 增量修补 + 种子数据.
/// </summary>
public class DatabaseSqliteModule : IModule
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseSqliteModule> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseSqliteModule"/> class.
    /// </summary>
    /// <param name="configuration">配置.</param>
    /// <param name="logger">日志.</param>
    public DatabaseSqliteModule(IConfiguration configuration, ILogger<DatabaseSqliteModule> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        var systemOptions = _configuration.GetSection("SData").Get<SystemOptions>()
            ?? throw new FormatException("The system configuration (SData section) cannot be loaded.");

        var dbPath = Path.Combine(AppConst.GetPath(), systemOptions.Database.Path.Replace('\\', '/'));
        var dbDirectory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
        }

        context.Services.AddDbContext<DatabaseContext, SqliteDatabaseContext>(options =>
        {
            options.UseSqlite($"Data Source={dbPath}");
        });

        // 启动时建库 + 增量修补 + 种子数据（内网单机部署，无需迁移框架）
        context.Services.AddHostedService<DatabaseInitializer>();
    }
}

/// <summary>
/// 数据库初始化后台服务.
/// </summary>
public class DatabaseInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly SystemOptions _systemOptions;
    private readonly SData.Infra.Services.IPasswordHasher _passwordHasher;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseInitializer"/> class.
    /// </summary>
    public DatabaseInitializer(IServiceScopeFactory scopeFactory, ILogger<DatabaseInitializer> logger, SystemOptions systemOptions, SData.Infra.Services.IPasswordHasher passwordHasher)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _systemOptions = systemOptions;
        _passwordHasher = passwordHasher;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var dbPath = ResolveDbPath();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        dbContext.Database.EnsureCreated();
        SchemaReconciler.Apply(dbContext, _logger);
        await SeedAsync(dbContext, cancellationToken);

        _logger.LogInformation("SQLite database initialized at {Path}", dbPath);
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string ResolveDbPath()
    {
        var dbPath = Path.Combine(AppConst.GetPath(), _systemOptions.Database.Path.Replace('\\', '/'));
        var dbDirectory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
        }

        return dbPath;
    }

    private async Task SeedAsync(DatabaseContext dbContext, CancellationToken cancellationToken)
    {
        var hasChanges = false;

        // 旧版本角色 "user" 统一升级为 "write"（已签发的旧 token 需重新登录生效）
        var legacyUsers = await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE user SET Role = 'write' WHERE Role = 'user'");
        if (legacyUsers > 0)
        {
            _logger.LogWarning("Migrated {Count} legacy role 'user' accounts to 'write'. Signed-in tokens need re-login.", legacyUsers);
        }

        if (!await dbContext.Users.AnyAsync(cancellationToken))
        {
            dbContext.Users.Add(new Entities.UserEntity
            {
                Id = 1,
                Username = "admin",
                Role = SDataRoles.Admin,
                PasswordHash = _passwordHasher.Hash(_systemOptions.Auth.InitialAdminPassword),
            });
            _logger.LogWarning("Seeded admin user with initial password from config (Auth:InitialAdminPassword). Change it after first login!");
            hasChanges = true;
        }

        var developmentHash = SData.Infra.Services.HashHelper.Sha256Hex("sdata-dev-ingest-key");
        foreach (var key in await dbContext.IngestKeys.Where(x => x.KeyHash == developmentHash && x.Enabled).ToListAsync(cancellationToken))
        {
            key.Enabled = false;
            hasChanges = true;
            _logger.LogWarning("Disabled known development ingestion key. Create a replacement in API & Agents.");
        }

        if (!_systemOptions.Ingest.RequireAuth)
            _logger.LogWarning("Ingest:RequireAuth=false is ignored. Server ingestion always requires an API key.");

        if (!string.IsNullOrWhiteSpace(_systemOptions.Ingest.InitialApiKey)
            && _systemOptions.Ingest.InitialApiKey != "sdata-dev-ingest-key"
            && !await dbContext.IngestKeys.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            dbContext.IngestKeys.Add(new Entities.IngestKeyEntity
            {
                Id = 2,
                Name = "default",
                KeyHash = SData.Infra.Services.HashHelper.Sha256Hex(_systemOptions.Ingest.InitialApiKey),
                Enabled = true,
            });
            _logger.LogWarning("Seeded default ingest key from config (Ingest:InitialApiKey).");
            hasChanges = true;
        }

        if (hasChanges)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
