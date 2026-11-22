using DuckDB.NET.Data;
using SData.Store.Core;
using SData.Store.Models;

namespace SData.Store;

/// <summary>消费统计仅查询内部生成的日表，所有筛选条件参数绑定.</summary>
public class MqConsumptionStore(DuckDbConnectionManager connections) : IMqConsumptionStore
{
    public async Task<IReadOnlyList<MqConsumptionRecord>> QueryAsync(DateTimeOffset from, DateTimeOffset to, long? instanceId, CancellationToken cancellationToken)
    {
        using var lease = await connections.ReadSourcesAsync(cancellationToken);
        var tables = connections.GetDayTables(TimeTables.MqConsumptionPrefix, from, to);
        if (tables.Count == 0) return [];
        using var connection = connections.CloneConnection();
        using var command = connection.CreateCommand();
        var queries = tables.Select(table => $"SELECT * FROM {table} WHERE ts >= $from AND ts < $to AND ($instance IS NULL OR instance_id = $instance)");
        command.CommandText = $"SELECT * FROM ({string.Join(" UNION ALL ", queries)}) samples ORDER BY ts DESC LIMIT 10000";
        command.Parameters.Add(new DuckDBParameter("from", from.UtcDateTime));
        command.Parameters.Add(new DuckDBParameter("to", to.UtcDateTime));
        command.Parameters.Add(new DuckDBParameter("instance", (object?)instanceId ?? DBNull.Value));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<MqConsumptionRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new MqConsumptionRecord
            {
                Timestamp = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc)),
                InstanceId = reader.GetInt64(1), SessionId = reader.GetString(2),
                Rule = reader.GetString(3), MatchMode = reader.GetString(4), State = reader.GetString(5),
                ReceivedMessages = reader.GetInt64(6), ReceivedBytes = reader.GetInt64(7),
                PersistedRecords = reader.GetInt64(8), ParseFailures = reader.GetInt64(9),
                SkippedMessages = reader.GetInt64(10), ConfirmedMessages = reader.GetInt64(11),
                ConfirmationFailures = reader.GetInt64(12), SamplingGaps = reader.GetInt64(13),
                DiagnosticsJson = reader.IsDBNull(14) ? null : reader.GetString(14),
            });
        }

        return records;
    }
}
