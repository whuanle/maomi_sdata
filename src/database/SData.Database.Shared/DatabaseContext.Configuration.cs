using Microsoft.EntityFrameworkCore;
using SData.Database.Audits;
using SData.Database.Entities;

namespace SData.Database;

/// <summary>
/// 实体映射配置.
/// </summary>
public partial class DatabaseContext
{
    private void ConfigureAudit(ModelBuilder modelBuilder)
    {
        // 软删除全局过滤器
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(IDeleteAudited).IsAssignableFrom(entityType.ClrType))
            {
                var parameter = System.Linq.Expressions.Expression.Parameter(entityType.ClrType, "p");
                var property = System.Linq.Expressions.Expression.Property(parameter, nameof(IDeleteAudited.IsDeleted));
                var filter = System.Linq.Expressions.Expression.Lambda(
                    System.Linq.Expressions.Expression.Equal(property, System.Linq.Expressions.Expression.Constant(0L)),
                    parameter);
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(filter);
            }
        }

        // 时间统一存 UTC
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, DateTimeOffset>(
                        v => v.ToUniversalTime(),
                        v => v));
                }
            }
        }
    }

    private void ConfigureEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserEntity>(e =>
        {
            e.ToTable("user");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
            e.Property(x => x.Role).HasMaxLength(16).IsRequired();
        });

        modelBuilder.Entity<IngestKeyEntity>(e =>
        {
            e.ToTable("ingest_key");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Prefix).HasColumnName("prefix").HasMaxLength(32).HasDefaultValue("");
            e.Property(x => x.ProtectedSecret).HasColumnName("protected_secret").IsRequired().HasDefaultValue("");
            e.HasIndex(x => x.KeyHash).IsUnique();
        });

        modelBuilder.Entity<AgentKeyEntity>(e =>
        {
            e.ToTable("agent_key");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Prefix).HasMaxLength(32).IsRequired();
            e.Property(x => x.ProtectedSecret).IsRequired().HasDefaultValue("");
            e.HasIndex(x => x.KeyHash).IsUnique();
            foreach (var property in e.Metadata.GetProperties())
            {
                e.Property(property.Name).HasColumnName(System.Text.RegularExpressions.Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
            }
        });

        modelBuilder.Entity<DashboardEntity>(e =>
        {
            e.ToTable("dashboard");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Description).HasMaxLength(256);
            e.Property(x => x.ConfigJson);
        });

        modelBuilder.Entity<SavedQueryEntity>(e =>
        {
            e.ToTable("saved_query");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired().HasDefaultValue("sql");
            e.Property(x => x.Description).HasMaxLength(256);
        });

        modelBuilder.Entity<DeviceEntity>(e =>
        {
            e.ToTable("device");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DeviceKey).IsUnique();
            e.Property(x => x.DeviceKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.Group).HasMaxLength(64);
            e.Property(x => x.TagsJson);
        });

        modelBuilder.Entity<SettingEntity>(e =>
        {
            e.ToTable("setting");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<MqNotificationStateEntity>(e =>
        {
            e.ToTable("mq_notification_state");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.InstanceId, x.Topic, x.ChannelId }).IsUnique();
            foreach (var property in e.Metadata.GetProperties())
                e.Property(property.Name).HasColumnName(System.Text.RegularExpressions.Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
        });

        modelBuilder.Entity<AlertRuleEntity>(e =>
        {
            e.ToTable("alert_rule");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Enabled, x.NextAttempt });
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<AlertEvaluationEntity>(e =>
        {
            e.ToTable("alert_evaluation");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RuleId, x.Version, x.WindowEnd }).IsUnique();
        });
        modelBuilder.Entity<AlertDeliveryEntity>(e =>
        {
            e.ToTable("alert_delivery");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RuleId, x.Version, x.WindowEnd, x.ChannelId, x.Kind }).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttempt });
        });
        foreach (var type in new[] { typeof(AlertRuleEntity), typeof(AlertEvaluationEntity), typeof(AlertDeliveryEntity) })
        {
            foreach (var property in modelBuilder.Entity(type).Metadata.GetProperties())
            {
                var name = System.Text.RegularExpressions.Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
                modelBuilder.Entity(type).Property(property.Name).HasColumnName(name);
            }
        }

        modelBuilder.Entity<NotificationChannelEntity>(e =>
        {
            e.ToTable("notification_channel");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(32).IsRequired();
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.ProtectedConfig).HasColumnName("protected_config").IsRequired();
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
            e.Property(x => x.CreateUserId).HasColumnName("create_user_id");
            e.Property(x => x.CreateTime).HasColumnName("create_time");
            e.Property(x => x.UpdateUserId).HasColumnName("update_user_id");
            e.Property(x => x.UpdateTime).HasColumnName("update_time");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted");
        });

        modelBuilder.Entity<RumProjectEntity>(e =>
        {
            e.ToTable("rum_project");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ProjectKey).IsUnique();
            e.Property(x => x.ProjectKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.DomainsJson);
        });
    }
}
