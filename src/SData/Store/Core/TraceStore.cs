using DuckDB.NET.Data;
using SData.Settings;
using SData.Store.Models;
using SData.Store.QueryDsl;

namespace SData.Store;

/// <summary>
/// 链路存储实现.
/// </summary>
public class TraceStore : ITraceStore
{
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly ISystemSettingsProvider _settings;
    private readonly ITableResolver? _tableResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="TraceStore"/> class.
    /// </summary>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="settings">系统设置（MaxQueryDays，保存即生效）.</param>
    public TraceStore(DuckDbConnectionManager connectionManager, ISystemSettingsProvider settings, ITableResolver? tableResolver = null)
    {
        _connectionManager = connectionManager;
        _settings = settings;
        _tableResolver = tableResolver;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TraceSummary>> QueryAsync(TraceQuery query, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var (start, end) = LogStore.NormalizeRange(query.Start, query.End, TimeSpan.FromMinutes(30));
        var resolved = _tableResolver == null
            ? new ResolvedTables(_connectionManager.GetDayTables(TimeTables.TracesPrefix, start, end), [])
            : await _tableResolver.ResolveAsync(DataDomain.Traces, start, end, cancellationToken: cancellationToken);
        var tables = resolved.LocalTables;
        if (tables.Count == 0 && resolved.ColdFiles.Count == 0 && resolved.ColdParquetGlobs.Count == 0)
        {
            return Array.Empty<TraceSummary>();
        }

        var parameters = new List<DuckDBParameter>
        {
            new("start", LogStore.ToDb(start)),
            new("end", LogStore.ToDb(end)),
            new("limit", Math.Clamp(query.Limit, 1, 200)),
        };

        var union = QueryDslCompiler.BuildBranchUnion(resolved, "trace_id, ts, duration_us, service, name, status");
        var outerWhere = new List<string>();
        if (!string.IsNullOrEmpty(query.Service))
        {
            parameters.Add(new DuckDBParameter("service", query.Service));
            outerWhere.Add("service = $service");
        }

        if (query.MinDurationUs > 0)
        {
            parameters.Add(new DuckDBParameter("minDuration", query.MinDurationUs.Value));
            outerWhere.Add("duration_us >= $minDuration");
        }

        if (query.HasError)
        {
            outerWhere.Add("error_count > 0");
        }

        var whereClause = outerWhere.Count > 0 ? " WHERE " + string.Join(" AND ", outerWhere) : string.Empty;
        var sql = $"""
            SELECT trace_id, service, root_name, span_count, error_count, start_ts, duration_us FROM (
                SELECT trace_id,
                       arg_min(service, ts) AS service,
                       arg_min(name, ts) AS root_name,
                       count(*) AS span_count,
                       count(*) FILTER (WHERE status = 'error') AS error_count,
                       min(ts) AS start_ts,
                       datediff('microseconds', min(ts), max(ts + to_microseconds(duration_us))) AS duration_us
                FROM ({union})
                GROUP BY trace_id
            ){whereClause}
            ORDER BY start_ts DESC
            LIMIT $limit
            """;

        return await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            var list = new List<TraceSummary>();
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(new TraceSummary
                {
                    TraceId = reader.GetString(0),
                    Service = reader.GetString(1),
                    RootName = reader.GetString(2),
                    SpanCount = reader.GetInt64(3),
                    ErrorCount = reader.GetInt64(4),
                    Start = LogStore.FromDb(reader, 5),
                    DurationUs = reader.GetInt64(6),
                });
            }

            return (IReadOnlyList<TraceSummary>)list;
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SpanRecord>> GetTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var start = now - TimeSpan.FromDays(Math.Clamp(_settings.GetRuntime().DuckDb.MaxQueryDays, 1, 90));
        var end = now + TimeSpan.FromHours(1);
        var resolved = _tableResolver == null
            ? new ResolvedTables(_connectionManager.GetDayTables(TimeTables.TracesPrefix, start, end), [])
            : await _tableResolver.ResolveAsync(DataDomain.Traces, start, end, cancellationToken: cancellationToken);
        var tables = resolved.LocalTables;
        if (tables.Count == 0 && resolved.ColdFiles.Count == 0 && resolved.ColdParquetGlobs.Count == 0)
        {
            return Array.Empty<SpanRecord>();
        }

        var parameters = new List<DuckDBParameter>
        {
            new("traceId", traceId),
            new("start", LogStore.ToDb(start)),
            new("end", LogStore.ToDb(end)),
        };

        var union = QueryDslCompiler.BuildBranchUnion(resolved, "trace_id, span_id, parent_span_id, ts, duration_us, service, name, kind, status, attributes, events, resource");
        var sql = $"SELECT trace_id, span_id, parent_span_id, ts, duration_us, service, name, kind, status, attributes, events, resource FROM ({union}) WHERE trace_id = $traceId ORDER BY ts";

        return await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            var list = new List<SpanRecord>();
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(new SpanRecord
                {
                    TraceId = reader.GetString(0),
                    SpanId = reader.GetString(1),
                    ParentSpanId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Timestamp = LogStore.FromDb(reader, 3),
                    DurationUs = reader.GetInt64(4),
                    Service = reader.GetString(5),
                    Name = reader.GetString(6),
                    Kind = reader.GetString(7),
                    Status = reader.GetString(8),
                    AttributesJson = reader.IsDBNull(9) ? null : reader.GetString(9),
                    EventsJson = reader.IsDBNull(10) ? null : reader.GetString(10),
                    ResourceJson = reader.IsDBNull(11) ? null : reader.GetString(11),
                });
            }

            return (IReadOnlyList<SpanRecord>)list;
        }, cancellationToken);
    }
}
