using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Database.Sqlite;
using Xunit;

namespace SData.Database.Tests;

public class AlertSchemaTests
{
    [Fact]
    public void Reconciler_AddsAlertTablesAndIndexes_Idempotently()
    {
        var cs = $"Data Source=alerts-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keeper = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        keeper.Open();
        using var db = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(cs).Options);
        db.Database.ExecuteSqlRaw("CREATE TABLE legacy_marker (id INTEGER PRIMARY KEY)");
        SchemaReconciler.Apply(db, NullLogger.Instance);
        SchemaReconciler.Apply(db, NullLogger.Instance);
        Assert.Empty(db.AlertRules.ToList());
        Assert.Empty(db.AlertEvaluations.ToList());
        Assert.Empty(db.AlertDeliveries.ToList());
        using var command = keeper.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name LIKE 'IX_alert_%'";
        Assert.Equal(4L, command.ExecuteScalar());
    }
}
