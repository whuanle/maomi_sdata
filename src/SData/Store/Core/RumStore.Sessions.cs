using SData.Store.QueryDsl;
using DuckDB.NET.Data;

namespace SData.Store.Core;

/// <summary>会话检索与事件时间线.</summary>
public partial class RumStore
{
    /// <inheritdoc/>
    public async Task<RumSessionPage> SearchSessionsAsync(string projectKey, DateTimeOffset from, DateTimeOffset to, RumSessionFilter filter, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var sources = new List<string>();
        var sessionTables = await ResolveSourcesAsync(DataDomain.RumSession, TimeTables.RumSessionPrefix, from, to, cancellationToken);
        var eventTables = await ResolveSourcesAsync(DataDomain.RumEvent, TimeTables.RumEventPrefix, from, to, cancellationToken);
        var replayTables = await ResolveSourcesAsync(DataDomain.RumReplay, TimeTables.RumReplayPrefix, from, to, cancellationToken);
        if (sessionTables.Count > 0)
        {
            sources.Add($"SELECT ts, project_key, session_id, user_agent, domain, NULL::VARCHAR AS page_path, 'pageview' AS kind FROM ({UnionAll(sessionTables, "ts, project_key, session_id, user_agent, domain")})");
        }

        if (eventTables.Count > 0)
        {
            sources.Add($"SELECT ts, project_key, session_id, NULL::VARCHAR AS user_agent, NULL::VARCHAR AS domain, page_path, event_type AS kind FROM ({UnionAll(eventTables, "ts, project_key, session_id, page_path, event_type")})");
        }

        if (replayTables.Count > 0)
        {
            sources.Add($"SELECT ts, project_key, session_id, NULL::VARCHAR AS user_agent, NULL::VARCHAR AS domain, NULL::VARCHAR AS page_path, 'replay' AS kind FROM ({UnionAll(replayTables, "ts, project_key, session_id")})");
        }

        if (sources.Count == 0)
        {
            return new RumSessionPage();
        }

        var parameters = new List<DuckDBParameter>
        {
            new("project_key", projectKey),
            new("from", LogStore.ToDb(from)),
            new("to", LogStore.ToDb(to)),
            new("search", filter.Search?.Trim() ?? string.Empty),
            new("limit", filter.PageSize),
            new("offset", (filter.Page - 1L) * filter.PageSize),
        };
        var conditions = new List<string>
        {
            "(contains(lower(session_id), lower($search)) OR contains(lower(coalesce(domain, '')), lower($search)) OR contains(lower(coalesce(page_path, '')), lower($search)) OR contains(lower(coalesce(user_agent, '')), lower($search)))",
        };
        if (filter.HasErrors.HasValue)
        {
            conditions.Add(filter.HasErrors.Value ? "error_count > 0" : "error_count = 0");
        }

        if (filter.HasReplay.HasValue)
        {
            conditions.Add(filter.HasReplay.Value ? "has_replay" : "NOT has_replay");
        }

        // 排序标识仅从白名单映射；用户值不拼入 SQL。
        var order = filter.Sort switch
        {
            "firstSeen" => "first_seen DESC",
            "duration" => "(last_seen - first_seen) DESC",
            "errors" => "error_count DESC",
            _ => "last_seen DESC",
        };
        var cte = $"""
            WITH activity AS (
                SELECT * FROM ({string.Join(" UNION ALL ", sources)})
                WHERE project_key = $project_key AND ts >= $from AND ts < $to
            ), summaries AS (
                SELECT session_id, min(ts) AS first_seen, max(ts) AS last_seen,
                       any_value(user_agent) AS user_agent, any_value(domain) AS domain,
                       arg_max(page_path, ts) AS page_path,
                       count(*) FILTER (kind = 'pageview') AS page_views,
                       count(*) FILTER (kind = 'error') AS error_count,
                       count(*) FILTER (kind <> 'replay') AS event_count,
                       bool_or(kind = 'replay') AS has_replay
                FROM activity GROUP BY session_id
            ), filtered AS (
                SELECT * FROM summaries WHERE {string.Join(" AND ", conditions)}
            )
            """;
        var result = new RumSessionPage();
        result.Count = await _connectionManager.QueryAsync($"{cte} SELECT count(*) FROM filtered", parameters, async reader =>
        {
            await reader.ReadAsync(cancellationToken);
            return reader.GetInt64(0);
        }, cancellationToken);
        var items = new List<RumSessionSummary>();
        await _connectionManager.QueryAsync($"{cte} SELECT session_id, first_seen, last_seen, user_agent, domain, page_path, page_views, error_count, event_count, has_replay FROM filtered ORDER BY {order}, session_id LIMIT $limit OFFSET $offset", parameters, async reader =>
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new RumSessionSummary
                {
                    SessionId = reader.GetString(0),
                    FirstSeen = LogStore.FromDb(reader, 1),
                    LastSeen = LogStore.FromDb(reader, 2),
                    UserAgent = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Domain = reader.IsDBNull(4) ? null : reader.GetString(4),
                    PagePath = reader.IsDBNull(5) ? null : reader.GetString(5),
                    PageViews = reader.GetInt64(6),
                    ErrorCount = reader.GetInt64(7),
                    EventCount = reader.GetInt64(8),
                    HasReplay = reader.GetBoolean(9),
                });
            }

            return 0;
        }, cancellationToken);
        result.Items = items;
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RumSessionEvent>> ListSessionEventsAsync(string projectKey, string sessionId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var tables = await ResolveSourcesAsync(DataDomain.RumEvent, TimeTables.RumEventPrefix, from, to, cancellationToken);
        if (tables.Count == 0)
        {
            return [];
        }

        var sql = $"""
            SELECT ts, event_type, event_name, page_path, value, payload
            FROM ({UnionAll(tables, "ts, project_key, session_id, event_type, event_name, page_path, value, payload")})
            WHERE project_key = $project_key AND session_id = $session_id AND ts >= $from AND ts < $to
            ORDER BY ts, event_type, event_name LIMIT 1000
            """;
        var parameters = new[] { new DuckDBParameter("project_key", projectKey), new DuckDBParameter("session_id", sessionId), new DuckDBParameter("from", LogStore.ToDb(from)), new DuckDBParameter("to", LogStore.ToDb(to)) };
        var items = new List<RumSessionEvent>();
        await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new RumSessionEvent
                {
                    Timestamp = LogStore.FromDb(reader, 0),
                    Type = reader.GetString(1),
                    Name = reader.IsDBNull(2) ? null : reader.GetString(2),
                    PagePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Value = reader.IsDBNull(4) ? null : Convert.ToDouble(reader.GetValue(4)),
                    Payload = reader.IsDBNull(5) ? null : reader.GetString(5),
                });
            }

            return 0;
        }, cancellationToken);
        return items;
    }
}
