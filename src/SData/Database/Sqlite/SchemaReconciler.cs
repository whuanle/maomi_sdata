using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SData.Database;

namespace SData.Database.Sqlite;

/// <summary>
/// Schema 自愈：对比 EF 模型与实际表结构，只增不改不删（新增缺失表与缺失列）.
/// EnsureCreated 对已存在的库不会补建后加的表，老库缺表由此处整表补齐.
/// </summary>
public static class SchemaReconciler
{
    /// <summary>
    /// 应用增量修补.
    /// </summary>
    /// <param name="dbContext">数据库上下文.</param>
    /// <param name="logger">日志.</param>
    public static void Apply(DatabaseContext dbContext, ILogger logger)
    {
        var connection = dbContext.Database.GetDbConnection();
        connection.Open();

        try
        {
            // EF 建库脚本按 ';'+换行 分隔语句（表定义内部无分号），统一换行后拆分，供缺失表整表补建
            var script = dbContext.Database.GenerateCreateScript().Replace("\r\n", "\n");
            var statements = script.Split(";\n", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            foreach (var entityType in dbContext.Model.GetEntityTypes())
            {
                var tableName = entityType.GetTableName();
                if (string.IsNullOrEmpty(tableName))
                {
                    continue;
                }

                var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT name FROM pragma_table_info(@table)";
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "@table";
                    parameter.Value = tableName;
                    command.Parameters.Add(parameter);

                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        existingColumns.Add(reader.GetString(0));
                    }
                }

                if (existingColumns.Count == 0)
                {
                    CreateMissingTable(connection, logger, statements, tableName);
                    continue;
                }

                foreach (var property in entityType.GetProperties())
                {
                    if (property.IsShadowProperty())
                    {
                        continue;
                    }

                    var columnName = property.GetColumnName(Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(tableName));
                    if (string.IsNullOrEmpty(columnName) || existingColumns.Contains(columnName))
                    {
                        continue;
                    }

                    var columnType = property.GetColumnType();
                    var isNullable = property.IsNullable ? "NULL" : "NOT NULL";
                    var defaultValue = string.Empty;
                    if (property.TryGetDefaultValue(out var defaultValueObject) && defaultValueObject != null)
                    {
                        defaultValue = defaultValueObject switch
                        {
                            bool b => $" DEFAULT {(b ? 1 : 0)}",
                            int i => $" DEFAULT {i}",
                            long l => $" DEFAULT {l}",
                            string s => $" DEFAULT '{s.Replace("'", "''")}'",
                            _ => string.Empty,
                        };
                    }

                    var alterSql = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnType} {isNullable}{defaultValue}";
                    logger.LogWarning("Schema reconcile: adding missing column {Table}.{Column}", tableName, columnName);
                    using var alterCommand = connection.CreateCommand();
                    alterCommand.CommandText = alterSql;
                    alterCommand.ExecuteNonQuery();
                }
            }
        }
        finally
        {
            connection.Close();
        }
    }

    /// <summary>
    /// 整表缺失补建：执行建库脚本中该表的 CREATE TABLE 与相关 CREATE INDEX（只增，不触碰已有表）.
    /// </summary>
    /// <param name="connection">已打开的连接.</param>
    /// <param name="logger">日志.</param>
    /// <param name="statements">建库脚本语句列表.</param>
    /// <param name="tableName">缺失表名.</param>
    private static void CreateMissingTable(System.Data.Common.DbConnection connection, ILogger logger, string[] statements, string tableName)
    {
        var executed = 0;
        foreach (var statement in statements)
        {
            var isTable = statement.StartsWith($"CREATE TABLE \"{tableName}\"", StringComparison.OrdinalIgnoreCase);
            var isIndex = statement.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)
                && statement.Contains($" ON \"{tableName}\"", StringComparison.Ordinal);
            if (!isTable && !isIndex)
            {
                continue;
            }

            using var createCommand = connection.CreateCommand();
            createCommand.CommandText = statement + ";";
            createCommand.ExecuteNonQuery();
            executed++;
        }

        if (executed > 0)
        {
            logger.LogWarning("Schema reconcile: created missing table {Table} ({Statements} statements)", tableName, executed);
        }
    }
}
