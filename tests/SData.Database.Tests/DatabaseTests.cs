using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Database.Sqlite;
using SData.Infra.Services;

namespace SData.Database.Tests;

/// <summary>
/// SQLite 元数据库测试：建库、审计字段、软删除、唯一约束.
/// </summary>
public class DatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDatabaseContext _dbContext;
    private readonly IPasswordHasher _passwordHasher = new Pbkdf2PasswordHasher();

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseTests"/> class.
    /// </summary>
    public DatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sdata-db-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _dbContext = new SqliteDatabaseContext(options);
        _dbContext.Database.EnsureCreated();
        SchemaReconciler.Apply(_dbContext, NullLogger.Instance);
    }

    /// <summary>
    /// 建库后全部表存在.
    /// </summary>
    [Fact]
    public void EnsureCreated_AllTablesExist()
    {
        var tables = new[] { "user", "ingest_key", "dashboard", "saved_query", "device", "setting" };
        foreach (var table in tables)
        {
            Assert.True(_dbContext.TableExists(table), $"table {table} should exist");
        }
    }

    /// <summary>
    /// 审计字段自动填充 + 用户名唯一.
    /// </summary>
    [Fact]
    public async Task AuditFields_AutoFilled_And_UniqueUsername()
    {
        _dbContext.Users.Add(new Database.Entities.UserEntity
        {
            Id = 100,
            Username = "tester",
            Role = "user",
            PasswordHash = _passwordHasher.Hash("password123"),
        });
        await _dbContext.SaveChangesAsync();

        var user = await _dbContext.Users.SingleAsync(x => x.Username == "tester");
        Assert.True(user.CreateTime > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(0, user.IsDeleted);

        _dbContext.Users.Add(new Database.Entities.UserEntity
        {
            Id = 101,
            Username = "tester",
            Role = "user",
            PasswordHash = _passwordHasher.Hash("password123"),
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => _dbContext.SaveChangesAsync());
    }

    /// <summary>
    /// 软删除：Remove 只置标记，查询过滤器自动排除.
    /// </summary>
    [Fact]
    public async Task SoftDelete_Filtered()
    {
        _dbContext.Devices.Add(new Database.Entities.DeviceEntity
        {
            Id = 200,
            DeviceKey = "dev-t1",
            Name = "测试设备",
        });
        await _dbContext.SaveChangesAsync();

        var device = await _dbContext.Devices.SingleAsync(x => x.DeviceKey == "dev-t1");
        _dbContext.Devices.Remove(device);
        await _dbContext.SaveChangesAsync();

        Assert.False(await _dbContext.Devices.AnyAsync(x => x.DeviceKey == "dev-t1"));

        // 底层数据仍在（未物理删除）
        var rawCount = await _dbContext.Devices.IgnoreQueryFilters().CountAsync(x => x.DeviceKey == "dev-t1");
        Assert.Equal(1, rawCount);
    }

    /// <summary>
    /// SchemaReconciler 补齐缺失列（模拟旧库缺列）.
    /// </summary>
    [Fact]
    public void SchemaReconciler_AddsMissingColumn()
    {
        // 直接删除一列模拟旧库（SQLite 3.35+ 支持 DROP COLUMN）
        _dbContext.Database.ExecuteSqlRaw("ALTER TABLE setting DROP COLUMN Value");

        SchemaReconciler.Apply(_dbContext, NullLogger.Instance);

        var columns = new List<string>();
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM pragma_table_info('setting')";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(0));
            }
        }

        Assert.Contains("Value", columns);
    }

    /// <summary>
    /// SchemaReconciler 整表补建缺失表（模拟老库缺后加的表：EnsureCreated 对已存在库不补新表）.
    /// 表名取模型实际表名（ColdArchiveEntity 无 ToTable 映射，默认 DbSet 名）.
    /// </summary>
    [Fact]
    public void SchemaReconciler_CreatesMissingTable()
    {
        // 删除后加的台账表模拟老库（该库从未跑过含此实体的 EnsureCreated）
        var tableName = _dbContext.Model.FindEntityType(typeof(Database.Entities.ColdArchiveEntity))!.GetTableName()!;
        _dbContext.Database.ExecuteSqlRaw($"DROP TABLE \"{tableName}\"");

        SchemaReconciler.Apply(_dbContext, NullLogger.Instance);

        Assert.True(_dbContext.TableExists(tableName));
        // 补建的表立即可用（EF 可正常查询与写入）
        Assert.Equal(0, _dbContext.ColdArchives.Count());
        _dbContext.ColdArchives.Add(new Database.Entities.ColdArchiveEntity
        {
            Id = 900,
            Domain = "logs",
            Day = "20260101",
            ObjectKey = "data/cold/logs/20260101.parquet",
            RowCount = 1,
            ArchivedTime = DateTimeOffset.UtcNow,
        });
        _dbContext.SaveChanges();
        Assert.Equal(1, _dbContext.ColdArchives.Count());
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _dbContext.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }
}
