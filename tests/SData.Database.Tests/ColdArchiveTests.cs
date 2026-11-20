using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Database.Entities;
using SData.Database.Sqlite;

namespace SData.Database.Tests;

/// <summary>
/// 冷存档台账实体测试（T512）：建表、审计字段自动填充、按域查询.
/// </summary>
public class ColdArchiveTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDatabaseContext _dbContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="ColdArchiveTests"/> class.
    /// </summary>
    public ColdArchiveTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sdata-coldarch-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _dbContext = new SqliteDatabaseContext(options);
        _dbContext.Database.EnsureCreated();
        SchemaReconciler.Apply(_dbContext, NullLogger.Instance);
    }

    /// <summary>台账插入与按域查询；审计字段由框架填充.</summary>
    [Fact]
    public async Task Insert_AndQueryByDomain()
    {
        _dbContext.ColdArchives.Add(new ColdArchiveEntity
        {
            Domain = "logs",
            Day = "20261001",
            ObjectKey = "s3://bkt/pfx/logs/20261001.parquet",
            RowCount = 123,
            ArchivedTime = DateTimeOffset.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        _dbContext.ColdArchives.Add(new ColdArchiveEntity
        {
            Domain = "metrics",
            Day = "20261001",
            ObjectKey = "data/cold/metrics/20261001.parquet",
            RowCount = 4,
            ArchivedTime = DateTimeOffset.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var logs = await _dbContext.ColdArchives.Where(x => x.Domain == "logs").ToListAsync();
        var row = Assert.Single(logs);
        Assert.Equal("20261001", row.Day);
        Assert.Equal(123, row.RowCount);
        Assert.True(row.Id > 0);
        Assert.True(row.CreateTime != default);

        var metrics = await _dbContext.ColdArchives.Where(x => x.Domain == "metrics").ToListAsync();
        Assert.Single(metrics);
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
