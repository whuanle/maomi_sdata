using DuckDB.NET.Data;
using SData.Infra.Configuration;
using SData.Store.Models;

namespace SData.Store;

/// <summary>
/// 遥测存储实现.
/// </summary>
public class TelemetryStore : ITelemetryStore
{
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly DuckDbOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryStore"/> class.
    /// </summary>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="systemOptions">系统配置.</param>
    public TelemetryStore(DuckDbConnectionManager connectionManager, SystemOptions systemOptions)
    {
        _connectionManager = connectionManager;
        _options = systemOptions.DuckDb;
    }

    /// <inheritdoc/>
    public async Task<SeriesResponse> QueryAsync(TelemetryQuery query, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var (start, end) = LogStore.NormalizeRange(query.Start, query.End, TimeSpan.FromMinutes(30));
        var stepSeconds = Math.Clamp(query.StepSeconds, 10, 86400);
        var tables = _connectionManager.GetDayTables(TimeTables.IotPrefix, start, end);
        var response = new SeriesResponse();
        if (tables.Count == 0)
        {
            return response;
        }

        var parameters = new List<DuckDBParameter>
        {
            new("deviceKey", query.DeviceKey),
            new("start", LogStore.ToDb(start)),
            new("end", LogStore.ToDb(end)),
            new("stepMs", (long)stepSeconds * 1000),
        };

        var where = new List<string> { "device_key = $deviceKey", "num_value IS NOT NULL" };
        var signals = query.Signals.Where(x => !string.IsNullOrEmpty(x)).Take(20).ToList();
        if (signals.Count > 0)
        {
            var names = new List<string>();
            for (var i = 0; i < signals.Count; i++)
            {
                parameters.Add(new DuckDBParameter($"sig{i}", signals[i]));
                names.Add($"$sig{i}");
            }

            where.Add($"signal IN ({string.Join(", ", names)})");
        }

        var union = LogStore.BuildUnion(tables, "device_key, signal, ts, num_value", parameters);
        var sql = $"""
            SELECT signal, bucket, {MetricStore.AggExpression(query.Aggregation, "num_value")} AS value FROM (
                SELECT signal, (epoch_ms(ts) // $stepMs) * $stepMs AS bucket, num_value
                FROM ({union})
                WHERE {string.Join(" AND ", where)}
            )
            GROUP BY signal, bucket
            ORDER BY bucket
            """;

        return await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            var bySignal = new Dictionary<string, SeriesResponse.Series>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                var signal = reader.GetString(0);
                var bucket = reader.GetInt64(1);
                var value = reader.IsDBNull(2) ? (double?)null : Convert.ToDouble(reader.GetValue(2));
                if (!bySignal.TryGetValue(signal, out var series))
                {
                    series = new SeriesResponse.Series { Name = signal, LabelsJson = $"{{\"device\":\"{query.DeviceKey.Replace("\"", "\\\"")}\"}}" };
                    bySignal[signal] = series;
                    response.SeriesList.Add(series);
                }

                series.Points.Add(new SeriesPoint { TimestampMs = bucket, Value = value });
            }

            return response;
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LatestValue>> LatestAsync(string deviceKey, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        // 只查最近 3 天的表
        var now = DateTimeOffset.UtcNow;
        var tables = _connectionManager.GetDayTables(TimeTables.IotPrefix, now - TimeSpan.FromDays(3), now + TimeSpan.FromHours(1));
        if (tables.Count == 0)
        {
            return Array.Empty<LatestValue>();
        }

        var parameters = new List<DuckDBParameter>
        {
            new("deviceKey", deviceKey),
            new("start", LogStore.ToDb(now - TimeSpan.FromDays(3))),
            new("end", LogStore.ToDb(now + TimeSpan.FromHours(1))),
        };

        var union = LogStore.BuildUnion(tables, "device_key, signal, ts, num_value, str_value", parameters);
        var sql = $"""
            SELECT signal, arg_max(num_value, ts) AS num_value, arg_max(str_value, ts) AS str_value, max(ts) AS ts
            FROM ({union})
            WHERE device_key = $deviceKey
            GROUP BY signal
            ORDER BY signal
            """;

        return await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            var list = new List<LatestValue>();
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(new LatestValue
                {
                    Signal = reader.GetString(0),
                    NumValue = reader.IsDBNull(1) ? null : Convert.ToDouble(reader.GetValue(1)),
                    StrValue = reader.IsDBNull(2) ? null : reader.GetString(2),
                    TimestampMs = LogStore.FromDb(reader, 3).ToUnixTimeMilliseconds(),
                });
            }

            return (IReadOnlyList<LatestValue>)list;
        }, cancellationToken);
    }
}
