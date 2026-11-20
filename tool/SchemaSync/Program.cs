using DuckDB.NET.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NetEscapades.Configuration.Yaml;
using SData.Database;
using SData.Database.Sqlite;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Store.Core;

// SchemaSync：SQLite 元数据库 + DuckDB 分析库的 Schema 管理工具。
// 用法：dotnet run --project tool/SchemaSync -- <command>
//   script  生成 SQLite 建库脚本到 asserts/sqlite/schema.sql（不含数据）
//   apply   对现有库执行 EnsureCreated + 增量修补（只增不删）
//   diff    报告现有库与模型的差异（不修改）
//   duck    对 DuckDB 应用内嵌 DDL（幂等）
var command = args.FirstOrDefault() ?? "script";
var configuration = BuildConfiguration();
var systemOptions = configuration.GetSection("SData").Get<SystemOptions>()
    ?? throw new FormatException("The system configuration (SData section) cannot be loaded.");
AppConst.ContentRoot = Directory.GetCurrentDirectory();

var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole());
var logger = loggerFactory.CreateLogger("SchemaSync");

switch (command)
{
    case "script":
        Script();
        break;
    case "apply":
        Apply();
        break;
    case "diff":
        Diff();
        break;
    case "duck":
        await DuckAsync();
        break;
    default:
        Console.WriteLine("unknown command: " + command);
        return 1;
}

return 0;

void Script()
{
    var options = new DbContextOptionsBuilder<DatabaseContext>()
        .UseSqlite($"Data Source={ResolveDbPath()}")
        .Options;
    using var dbContext = new SqliteDatabaseContext(options);
    var script = dbContext.Database.GenerateCreateScript();
    var target = Path.Combine(Directory.GetCurrentDirectory(), "asserts", "sqlite", "schema.sql");
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.WriteAllText(target, script);
    Console.WriteLine("sqlite schema written: " + target);
}

void Apply()
{
    var options = new DbContextOptionsBuilder<DatabaseContext>()
        .UseSqlite($"Data Source={ResolveDbPath()}")
        .Options;
    using var dbContext = new SqliteDatabaseContext(options);
    dbContext.Database.EnsureCreated();
    SchemaReconciler.Apply(dbContext, logger);
    Console.WriteLine("sqlite schema applied: " + ResolveDbPath());
}

void Diff()
{
    var options = new DbContextOptionsBuilder<DatabaseContext>()
        .UseSqlite($"Data Source={ResolveDbPath()}")
        .Options;
    using var dbContext = new SqliteDatabaseContext(options);
    if (!File.Exists(ResolveDbPath()))
    {
        Console.WriteLine("database file does not exist, apply will create it");
        return;
    }

    SchemaReconciler.Apply(dbContext, logger);
    Console.WriteLine("diff done (any missing columns above have been reported as warnings)");
}

async Task DuckAsync()
{
    var dbPath = Path.Combine(AppConst.GetPath(), systemOptions.DuckDb.Path.Replace('\\', '/'));
    var dbDirectory = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dbDirectory))
    {
        Directory.CreateDirectory(dbDirectory);
    }

    await using var connection = new DuckDBConnection($"Data Source={dbPath}");
    await connection.OpenAsync();

    var assembly = typeof(DuckDbConnectionManager).Assembly;
    var scripts = assembly.GetManifestResourceNames()
        .Where(x => x.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x, StringComparer.Ordinal);

    foreach (var script in scripts)
    {
        await using var stream = assembly.GetManifestResourceStream(script);
        if (stream == null)
        {
            continue;
        }

        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        foreach (var statement in content.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            await using var dbCommand = connection.CreateCommand();
            dbCommand.CommandText = statement;
            await dbCommand.ExecuteNonQueryAsync();
        }

        Console.WriteLine("applied: " + script);
    }

    Console.WriteLine("duckdb schema applied: " + dbPath);
}

string ResolveDbPath()
{
    var dbPath = Path.Combine(AppConst.GetPath(), systemOptions.Database.Path.Replace('\\', '/'));
    var dbDirectory = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dbDirectory))
    {
        Directory.CreateDirectory(dbDirectory);
    }

    return dbPath;
}

IConfiguration BuildConfiguration()
{
    var builder = new ConfigurationBuilder();
    var file = Environment.GetEnvironmentVariable("SDATA_FILE");
    var candidates = new List<string>();
    if (!string.IsNullOrEmpty(file))
    {
        candidates.Add(file);
    }

    candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "configs", "system.yaml"));
    candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "configs", "system.json"));

    var configFile = candidates.FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException("System config not found (configs/system.yaml). Set SDATA_FILE to override.");

    var extension = Path.GetExtension(configFile).ToLowerInvariant();
    if (extension is ".yaml" or ".yml")
    {
        builder.AddYamlFile(configFile);
    }
    else
    {
        builder.AddJsonFile(configFile);
    }

    return builder.Build();
}
