using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Database.Sqlite;
using Xunit;

namespace SData.Database.Tests;

public class NotificationSchemaTests
{
    [Fact]
    public void Reconciler_AddsNotificationTable_ToExistingDatabase_Idempotently()
    {
        var connectionString = $"Data Source=notification-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        keepAlive.Open();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        using var database = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(connection).Options);
        database.Database.ExecuteSqlRaw("CREATE TABLE legacy_marker (id INTEGER PRIMARY KEY)");
        SchemaReconciler.Apply(database, NullLogger.Instance);
        SchemaReconciler.Apply(database, NullLogger.Instance);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('notification_channel') WHERE name IN ('id', 'protected_config', 'version', 'create_user_id', 'is_deleted')";
        Assert.Equal(5L, command.ExecuteScalar());
        Assert.Empty(database.NotificationChannels.ToList());
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('mq_notification_state') WHERE name IN ('id', 'instance_id', 'topic', 'channel_id', 'suppressed_until', 'suppressed_count')";
        Assert.Equal(6L, command.ExecuteScalar());
        Assert.Empty(database.MqNotificationStates.ToList());
    }
}
