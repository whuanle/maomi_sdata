using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Database.Sqlite;

namespace SData.Database.Tests;

/// <summary>
/// 保存查询（saved_query）测试：Kind/Definition 列、旧库增量修补、按类型过滤.
/// </summary>
public class SavedQueryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDatabaseContext _dbContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="SavedQueryTests"/> class.
    /// </summary>
    public SavedQueryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sdata-sq-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _dbContext = new SqliteDatabaseContext(options);
        _dbContext.Database.EnsureCreated();
        SchemaReconciler.Apply(_dbContext, NullLogger.Instance);
    }

    /// <summary>新库 saved_query 含 kind/definition 列，默认 kind=sql.</summary>
    [Fact]
    public async Task NewDatabase_HasKindAndDefinition_DefaultSql()
    {
        _dbContext.SavedQueries.Add(new Database.Entities.SavedQueryEntity
        {
            Id = 1,
            Name = "q1",
            SqlText = "SELECT 1",
        });
        await _dbContext.SaveChangesAsync();

        var saved = await _dbContext.SavedQueries.SingleAsync(x => x.Id == 1);
        Assert.Equal("sql", saved.Kind);
        Assert.Equal(string.Empty, saved.Definition);

        var kind = ColumnExists("saved_query", "Kind");
        var definition = ColumnExists("saved_query", "Definition");
        Assert.True(kind, "saved_query.kind should exist");
        Assert.True(definition, "saved_query.definition should exist");
    }

    /// <summary>SchemaReconciler 给旧库补 kind/definition 列（模拟升级）.</summary>
    [Fact]
    public void SchemaReconciler_AddsKindAndDefinitionColumns()
    {
        _dbContext.Database.ExecuteSqlRaw("ALTER TABLE saved_query DROP COLUMN Kind");
        _dbContext.Database.ExecuteSqlRaw("ALTER TABLE saved_query DROP COLUMN Definition");

        SchemaReconciler.Apply(_dbContext, NullLogger.Instance);

        Assert.True(ColumnExists("saved_query", "Kind"), "reconciler should add saved_query.kind");
        Assert.True(ColumnExists("saved_query", "Definition"), "reconciler should add saved_query.definition");
    }

    /// <summary>kind=search 保存搜索快照：Definition 持久化、EF 层按 Kind 过滤.</summary>
    [Fact]
    public async Task SearchKind_PersistedAndFiltered()
    {
        const string definition = """{"domain":"traces","query":"service:api duration_us:>100000","rangeMs":900000}""";
        _dbContext.SavedQueries.Add(new Database.Entities.SavedQueryEntity
        {
            Id = 2,
            Name = "慢链路搜索",
            Kind = "search",
            Definition = definition,
        });
        _dbContext.SavedQueries.Add(new Database.Entities.SavedQueryEntity
        {
            Id = 3,
            Name = "错误日志搜索",
            Kind = "search",
            Definition = """{"domain":"logs","query":"severity:(ERROR,FATAL) timeout","rangeMs":900000}""",
        });
        _dbContext.SavedQueries.Add(new Database.Entities.SavedQueryEntity
        {
            Id = 4,
            Name = "分析SQL",
            SqlText = "SELECT count(*) FROM logs_x",
        });
        await _dbContext.SaveChangesAsync();

        var searches = await _dbContext.SavedQueries.AsNoTracking()
            .Where(x => x.Kind == "search").OrderByDescending(x => x.Id).ToListAsync();
        Assert.Equal(2, searches.Count);
        Assert.All(searches, item => Assert.Equal(string.Empty, item.SqlText));
        Assert.Equal(definition, searches.Single(x => x.Id == 2).Definition);

        var sqlOnly = await _dbContext.SavedQueries.AsNoTracking()
            .Where(x => x.Kind == "sql").ToListAsync();
        var single = Assert.Single(sqlOnly);
        Assert.Equal(string.Empty, single.Definition);
    }

    /// <summary>检查物理列是否存在.</summary>
    private bool ColumnExists(string tableName, string columnName)
    {
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{tableName}')";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(0), columnName, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
