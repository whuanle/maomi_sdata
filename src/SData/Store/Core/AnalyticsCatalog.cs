using SData.Store.Core;

namespace SData.Store;

/// <summary>实时读取 DuckDB 目录，不依赖写入服务的表缓存.</summary>
public class AnalyticsCatalog(DuckDbConnectionManager manager) : IAnalyticsCatalog
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<AnalyticsTableItem>> GetTablesAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await manager.ReadSourcesAsync(cancellationToken);
        return await manager.QueryAsync<IReadOnlyList<AnalyticsTableItem>>(
            """
            SELECT c.table_schema, c.table_name, c.column_name, c.data_type, c.is_nullable
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_catalog = c.table_catalog AND t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE t.table_type IN ('BASE TABLE', 'VIEW')
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """, null, async reader =>
            {
                var tables = new List<AnalyticsTableItem>();
                AnalyticsTableItem? current = null;
                while (await reader.ReadAsync(cancellationToken))
                {
                    var schema = reader.GetString(0);
                    var name = reader.GetString(1);
                    // 视图门面改造：跳过伴生物理表（数据窄表/resource 字典表），天对象视图本身正常列出
                    if (IsCompanionTable(name))
                    {
                        continue;
                    }

                    if (current == null || current.Schema != schema || current.Name != name)
                    {
                        current = new AnalyticsTableItem { Schema = schema, Name = name };
                        foreach (var (prefix, domain) in new[]
                        {
                            (TimeTables.LogsPrefix, "logs"), (TimeTables.TracesPrefix, "traces"),
                            (TimeTables.MetricsPrefix, "metrics"), (TimeTables.IotPrefix, "iot"),
                            (TimeTables.RumSessionPrefix, "sessions"),
                        })
                        {
                            var day = TimeTables.ParseDay(name);
                            if (day.HasValue && TimeTables.Name(prefix, day.Value) == name)
                            {
                                current.Domain = domain;
                                current.Day = day;
                                break;
                            }
                        }

                        tables.Add(current);
                    }

                    current.Columns.Add(new AnalyticsColumnItem
                    {
                        Name = reader.GetString(2),
                        Type = reader.GetString(3),
                        Nullable = reader.GetString(4) == "YES",
                    });
                }

                return tables;
            }, cancellationToken);
    }

    /// <summary>logs/traces 视图门面的伴生物理表（数据窄表/resource 字典表及其冻结快照）不对 MCP 目录暴露.</summary>
    private static bool IsCompanionTable(string name)
    {
        return name.StartsWith(TimeTables.LogDataPrefix, StringComparison.Ordinal)
            || name.StartsWith(TimeTables.LogResPrefix, StringComparison.Ordinal)
            || name.StartsWith(TimeTables.TraceDataPrefix, StringComparison.Ordinal)
            || name.StartsWith(TimeTables.TraceResPrefix, StringComparison.Ordinal);
    }
}
