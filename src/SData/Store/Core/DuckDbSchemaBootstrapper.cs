using System.Reflection;
using System.Text;

namespace SData.Store.Core;

/// <summary>
/// DuckDB Schema 引导：执行内嵌 DDL 脚本（幂等）.
/// </summary>
public class DuckDbSchemaBootstrapper
{
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly ILogger<DuckDbSchemaBootstrapper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DuckDbSchemaBootstrapper"/> class.
    /// </summary>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="logger">日志.</param>
    public DuckDbSchemaBootstrapper(DuckDbConnectionManager connectionManager, ILogger<DuckDbSchemaBootstrapper> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>
    /// 应用全部内嵌 DDL.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var script in scripts)
        {
            await using var stream = assembly.GetManifestResourceStream(script);
            if (stream == null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = await reader.ReadToEndAsync(cancellationToken);

            foreach (var statement in SplitStatements(content))
            {
                await _connectionManager.ExecuteWriteAsync(statement, cancellationToken: cancellationToken);
            }

            _logger.LogDebug("Applied DuckDB DDL script {Script}", script);
        }

        await MigrateLegacyLogTablesAsync(cancellationToken);
        foreach (var existing in _connectionManager.GetManagedTables(TimeTables.MqConsumptionPrefix))
        {
            if (TimeTables.ParseDay(existing) is not { } day) continue;
            var table = TimeTables.Name(TimeTables.MqConsumptionPrefix, day);
            if (existing != table) continue;
            await _connectionManager.ExecuteWriteAsync($"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS diagnostics VARCHAR", cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// 旧版 logs 天表补列：source（T121）与 id（行级雪花 id，幂等去重/行级定位）.
    /// ALTER TABLE ADD COLUMN IF NOT EXISTS 幂等；存量行补列为 NULL（历史行无行级身份）.
    /// 仅作用于物理表（table_type='BASE TABLE'）——视图门面天对象（logs_yyyymmdd 视图）不可 ALTER.
    /// 表名仅取自库内 information_schema 枚举并通过 TimeTables.ParseDay 形态校验（非用户输入）.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    private async Task MigrateLegacyLogTablesAsync(CancellationToken cancellationToken)
    {
        var tableNames = new List<string>();
        await foreach (var name in _connectionManager.QueryEachAsync(
            "SELECT table_name FROM information_schema.tables WHERE table_name LIKE 'logs\\_%' ESCAPE '\\' AND table_type = 'BASE TABLE'",
            cancellationToken))
        {
            if (TimeTables.ParseDay(name) != null)
            {
                tableNames.Add(name);
            }
        }

        foreach (var table in tableNames)
        {
            await _connectionManager.ExecuteWriteAsync(
                $"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS source VARCHAR",
                cancellationToken: cancellationToken);
            await _connectionManager.ExecuteWriteAsync(
                $"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS id BIGINT",
                cancellationToken: cancellationToken);
            _connectionManager.InvalidateLogColumns(table);
            _logger.LogDebug("Migrated legacy log table {Table}: added source/id columns", table);
        }

        if (tableNames.Count > 0)
        {
            _logger.LogInformation("Migrated {Count} legacy log tables with source/id columns", tableNames.Count);
        }
    }

    private static IEnumerable<string> SplitStatements(string content)
    {
        var statements = content.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var statement in statements)
        {
            var withoutComments = string.Join('\n', statement.Split('\n')
                .Where(line => !line.TrimStart().StartsWith("--")));
            if (!string.IsNullOrWhiteSpace(withoutComments))
            {
                yield return withoutComments;
            }
        }
    }
}
