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
            WHERE t.table_type = 'BASE TABLE'
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """, null, async reader =>
            {
                var tables = new List<AnalyticsTableItem>();
                AnalyticsTableItem? current = null;
                while (await reader.ReadAsync(cancellationToken))
                {
                    var schema = reader.GetString(0);
                    var name = reader.GetString(1);
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
}
