using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SData.Database.Audits;
using SData.Database.Entities;
using SData.Infra.Services;

namespace SData.Database;

/// <summary>
/// 元数据库上下文（SQLite）.
/// </summary>
public partial class DatabaseContext : DbContext
{
    private readonly IUserContextProvider? _userContextProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseContext"/> class.
    /// </summary>
    /// <param name="options">配置.</param>
    /// <param name="userContextProvider">用户上下文（审计字段自动填充）.</param>
    public DatabaseContext(DbContextOptions options, IUserContextProvider? userContextProvider = null)
        : base(options)
    {
        _userContextProvider = userContextProvider;

        // 审计字段自动填充（订阅变更事件，见 OnEntityTracked/OnEntityStateChanged）
        ChangeTracker.Tracked += OnEntityTracked;
        ChangeTracker.StateChanged += OnEntityStateChanged;
    }

    /// <summary>
    /// 用户.
    /// </summary>
    public DbSet<UserEntity> Users { get; set; }

    /// <summary>
    /// 接入 API Key.
    /// </summary>
    public DbSet<IngestKeyEntity> IngestKeys { get; set; }

    /// <summary>Agent 只读访问凭据.</summary>
    public DbSet<AgentKeyEntity> AgentKeys { get; set; }

    /// <summary>
    /// 仪表盘.
    /// </summary>
    public DbSet<DashboardEntity> Dashboards { get; set; }

    /// <summary>
    /// 保存的查询.
    /// </summary>
    public DbSet<SavedQueryEntity> SavedQueries { get; set; }

    /// <summary>
    /// 设备.
    /// </summary>
    public DbSet<DeviceEntity> Devices { get; set; }

    /// <summary>
    /// 系统配置.
    /// </summary>
    public DbSet<SettingEntity> Settings { get; set; }

    public DbSet<MqNotificationStateEntity> MqNotificationStates { get; set; }

    public DbSet<AlertRuleEntity> AlertRules { get; set; }
    public DbSet<AlertEvaluationEntity> AlertEvaluations { get; set; }
    public DbSet<AlertDeliveryEntity> AlertDeliveries { get; set; }

    /// <summary>外部通知渠道.</summary>
    public DbSet<NotificationChannelEntity> NotificationChannels { get; set; }

    /// <summary>
    /// RUM 监测项目（T611）.
    /// </summary>
    public DbSet<RumProjectEntity> RumProjects { get; set; }

    /// <summary>
    /// 冷存档台账（T512：域/天/对象键/行数，幂等归档依据）.
    /// </summary>
    public DbSet<ColdArchiveEntity> ColdArchives { get; set; }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureAudit(modelBuilder);
        ConfigureEntities(modelBuilder);
    }

    private void OnEntityTracked(object? sender, EntityTrackedEventArgs e)
    {
        if (!e.FromQuery && e.Entry.State == EntityState.Added && e.Entry.Entity is ICreationAudited creationAudited)
        {
            creationAudited.CreateTime = DateTimeOffset.UtcNow;
            creationAudited.CreateUserId = _userContextProvider == null ? 0 : GetUserId();
        }

        if (!e.FromQuery && e.Entry.State == EntityState.Added && e.Entry.Entity is IModificationAudited modificationAudited)
        {
            modificationAudited.UpdateTime = DateTimeOffset.UtcNow;
            modificationAudited.UpdateUserId = _userContextProvider == null ? 0 : GetUserId();
        }
    }

    private void OnEntityStateChanged(object? sender, EntityStateChangedEventArgs e)
    {
        if (e.NewState == EntityState.Modified && e.Entry.Entity is IModificationAudited modificationAudited)
        {
            modificationAudited.UpdateTime = DateTimeOffset.UtcNow;
            modificationAudited.UpdateUserId = _userContextProvider == null ? 0 : GetUserId();
        }

        // 软删除：保留 Modified 状态而非 Deleted
        if (e.NewState == EntityState.Deleted && e.Entry.Entity is IDeleteAudited deleteAudited)
        {
            if (deleteAudited.IsDeleted == 0)
            {
                deleteAudited.IsDeleted = DateTimeOffset.UtcNow.Ticks;
            }

            e.Entry.State = EntityState.Modified;
        }
    }

    private long GetUserId()
    {
        return _userContextProvider == null ? 0 : _userContextProvider.GetCurrentUserId();
    }
}
