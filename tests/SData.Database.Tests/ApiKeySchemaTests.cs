using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Database.Entities;
using SData.Database.Sqlite;
using SData.Infra.Configuration;
using SData.Infra.Services;
using Xunit;

namespace SData.Database.Tests;

public class ApiKeySchemaTests
{
    [Fact]
    public void Upgrade_AddsAgentTableAndPrefix_Idempotently()
    {
        var connectionString = $"Data Source=agent-schema-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        keepAlive.Open();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        using var db = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(connection).Options);
        db.Database.ExecuteSqlRaw("CREATE TABLE ingest_key (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, KeyHash TEXT NOT NULL, Enabled INTEGER NOT NULL, CreateUserId INTEGER NOT NULL, CreateTime TEXT NOT NULL, UpdateUserId INTEGER NOT NULL, UpdateTime TEXT NOT NULL, IsDeleted INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("CREATE TABLE agent_key (id INTEGER PRIMARY KEY, name TEXT NOT NULL, key_hash TEXT NOT NULL, prefix TEXT NOT NULL, enabled INTEGER NOT NULL, create_user_id INTEGER NOT NULL, create_time TEXT NOT NULL, update_user_id INTEGER NOT NULL, update_time TEXT NOT NULL, is_deleted INTEGER NOT NULL)");
        db.Database.ExecuteSqlRaw("INSERT INTO ingest_key VALUES (100, 'legacy', 'legacy-hash', 1, 0, '2026-11-20T00:00:00Z', 0, '2026-11-20T00:00:00Z', 0)");
        SchemaReconciler.Apply(db, NullLogger.Instance);
        SchemaReconciler.Apply(db, NullLogger.Instance);
        Assert.Empty(db.AgentKeys.ToList());
        var legacy = db.IngestKeys.Single();
        Assert.Equal("legacy-hash", legacy.KeyHash);
        Assert.Equal(string.Empty, legacy.ProtectedSecret);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ingest_key') WHERE name='prefix'";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT dflt_value FROM pragma_table_info('agent_key') WHERE name='protected_secret'";
        Assert.Equal("''", command.ExecuteScalar());
    }

    [Fact]
    public async Task Initialization_DoesNotResurrectRevokedKey()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var services = new ServiceCollection();
        services.AddDbContext<DatabaseContext>(o => o.UseSqlite("Data Source=" + file));
        await using var provider = services.BuildServiceProvider();
        var options = new SystemOptions { Database = { Path = file } };
        options.Ingest.InitialApiKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var initializer = new DatabaseInitializer(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DatabaseInitializer>.Instance, options, new Pbkdf2PasswordHasher());
        try
        {
            await initializer.StartAsync(default);
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
                db.IngestKeys.Remove(await db.IngestKeys.SingleAsync());
                await db.SaveChangesAsync();
            }
            await initializer.StartAsync(default);
            using var finalScope = provider.CreateScope();
            Assert.Empty(await finalScope.ServiceProvider.GetRequiredService<DatabaseContext>().IngestKeys.ToListAsync());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(file); }
    }
}
