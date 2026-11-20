using DuckDB.NET.Data;
using SData.Store;
using SData.Store.QueryDsl;

namespace SData.Store.Core;

/// <summary>
/// RUM 查询实现：rum_event/rum_session 天表参数化聚合（T613；表名一律来自 TimeTables，外部值全部参数绑定）.
/// </summary>
public partial class RumStore : IRumStore
{
    /// <summary>热力目标 TopN 上限.</summary>
    private const int HeatmapTopTargets = 10;

    private readonly DuckDbConnectionManager _connectionManager;
    private readonly ITableResolver? _tableResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="RumStore"/> class.
    /// </summary>
    /// <param name="connectionManager">DuckDB 连接管理器.</param>
    public RumStore(DuckDbConnectionManager connectionManager, ITableResolver? tableResolver = null)
    {
        _connectionManager = connectionManager;
        _tableResolver = tableResolver;
    }

    /// <inheritdoc/>
    public async Task<RumPerformanceAggregates> AggregatePerformanceAsync(string projectKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var result = new RumPerformanceAggregates();
        var parameters = new[] { new DuckDBParameter("project_key", projectKey), new DuckDBParameter("from", LogStore.ToDb(from)), new DuckDBParameter("to", LogStore.ToDb(to)) };

        // PV/UV：pageview 事件落 rum_session 表（每行一次浏览）
        var sessionTables = await ResolveSourcesAsync(DataDomain.RumSession, TimeTables.RumSessionPrefix, from, to, cancellationToken);
        if (sessionTables.Count > 0)
        {
            var sql = $"SELECT count(*) AS pv, count(DISTINCT session_id) AS uv FROM ({UnionAll(sessionTables, "session_id, project_key, ts")}) WHERE project_key = $project_key AND ts >= $from AND ts < $to";
            (result.PageViews, result.UniqueVisitors) = await _connectionManager.QueryAsync(sql, parameters, async reader =>
            {
                await reader.ReadAsync(cancellationToken);
                return (reader.GetInt64(0), reader.GetInt64(1));
            }, cancellationToken);
        }

        // Web Vitals p75：rum_event 表 vital 事件按 event_name 分位
        var eventTables = await ResolveSourcesAsync(DataDomain.RumEvent, TimeTables.RumEventPrefix, from, to, cancellationToken);
        if (eventTables.Count > 0)
        {
            var sql = $"""
                SELECT event_name, percentile_cont(0.75) WITHIN GROUP (ORDER BY value) AS p75
                FROM ({UnionAll(eventTables, "event_type, project_key, event_name, value, ts")})
                WHERE project_key = $project_key AND event_type = 'vital' AND value IS NOT NULL AND ts >= $from AND ts < $to
                GROUP BY event_name
                """;
            await _connectionManager.QueryAsync(sql, parameters, async reader =>
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var name = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
                    result.VitalP75[name] = Convert.ToDouble(reader.GetValue(1));
                }

                return 0;
            }, cancellationToken);
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RumErrorAggregate>> AggregateErrorsAsync(string projectKey, DateTimeOffset from, DateTimeOffset to, int topN, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var eventTables = await ResolveSourcesAsync(DataDomain.RumEvent, TimeTables.RumEventPrefix, from, to, cancellationToken);
        if (eventTables.Count == 0)
        {
            return [];
        }

        var sql = $"""
            SELECT coalesce(event_name, 'unknown') AS name, count(*) AS count
            FROM ({UnionAll(eventTables, "event_type, project_key, event_name, ts")})
            WHERE project_key = $project_key AND event_type = 'error' AND ts >= $from AND ts < $to
            GROUP BY 1 ORDER BY count DESC, name LIMIT $top_n
            """;
        var parameters = new[] { new DuckDBParameter("project_key", projectKey), new DuckDBParameter("from", LogStore.ToDb(from)), new DuckDBParameter("to", LogStore.ToDb(to)), new DuckDBParameter("top_n", topN) };
        var items = new List<RumErrorAggregate>();
        await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new RumErrorAggregate { Name = reader.GetString(0), Count = reader.GetInt64(1) });
            }

            return 0;
        }, cancellationToken);
        return items;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RumSessionSummary>> ListSessionsAsync(string projectKey, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var sessionTables = await ResolveSourcesAsync(DataDomain.RumSession, TimeTables.RumSessionPrefix, from, to, cancellationToken);
        if (sessionTables.Count == 0)
        {
            return [];
        }

        var sql = $"""
            SELECT session_id, max(ts) AS last_seen, any_value(user_agent) AS user_agent, any_value(domain) AS domain
            FROM ({UnionAll(sessionTables, "session_id, ts, user_agent, domain, project_key")})
            WHERE project_key = $project_key AND ts >= $from AND ts < $to
            GROUP BY session_id ORDER BY last_seen DESC LIMIT $limit
            """;
        var parameters = new[] { new DuckDBParameter("project_key", projectKey), new DuckDBParameter("from", LogStore.ToDb(from)), new DuckDBParameter("to", LogStore.ToDb(to)), new DuckDBParameter("limit", limit) };
        var items = new List<RumSessionSummary>();
        await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new RumSessionSummary
                {
                    SessionId = reader.GetString(0),
                    LastSeen = LogStore.FromDb(reader, 1),
                    UserAgent = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Domain = reader.IsDBNull(3) ? null : reader.GetString(3),
                });
            }

            return 0;
        }, cancellationToken);
        return items;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RumReplayChunk>> GetReplayAsync(string projectKey, string sessionId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var replayTables = await ResolveSourcesAsync(DataDomain.RumReplay, TimeTables.RumReplayPrefix, from, to, cancellationToken);
        if (replayTables.Count == 0)
        {
            return [];
        }

        var sql = $"""
            SELECT seq, ts, blob_base64
            FROM ({UnionAll(replayTables, "seq, ts, blob_base64, session_id, project_key")})
            WHERE project_key = $project_key AND session_id = $session_id AND ts >= $from AND ts < $to
            ORDER BY seq
            """;
        var parameters = new[] { new DuckDBParameter("project_key", projectKey), new DuckDBParameter("session_id", sessionId), new DuckDBParameter("from", LogStore.ToDb(from)), new DuckDBParameter("to", LogStore.ToDb(to)) };
        var items = new List<RumReplayChunk>();
        await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new RumReplayChunk
                {
                    Seq = reader.GetInt64(0),
                    Timestamp = LogStore.FromDb(reader, 1),
                    BlobBase64 = reader.GetString(2),
                });
            }

            return 0;
        }, cancellationToken);
        return items;
    }

    /// <summary>
    /// 点击热力聚合：文档绝对坐标（pageX/docW）优先，回退存量视口坐标（x/pageW）；
    /// pagePath 过滤经参数绑定（T616）.
    /// </summary>
    /// <inheritdoc/>
    public async Task<RumHeatmapAggregates> AggregateHeatmapAsync(string projectKey, DateTimeOffset from, DateTimeOffset to, int gridCells, string? pagePath = null, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var eventTables = await ResolveSourcesAsync(DataDomain.RumEvent, TimeTables.RumEventPrefix, from, to, cancellationToken);
        if (eventTables.Count == 0)
        {
            return new RumHeatmapAggregates();
        }

        var grid = Math.Clamp(gridCells, 2, 100);
        var pathFilter = string.IsNullOrEmpty(pagePath) ? string.Empty : "AND page_path = $page_path";
        var parameters = new List<DuckDBParameter>
        {
            new("project_key", projectKey),
            new("top_n", HeatmapTopTargets),
            new("from", LogStore.ToDb(from)),
            new("to", LogStore.ToDb(to)),
        };
        if (!string.IsNullOrEmpty(pagePath))
        {
            parameters.Add(new DuckDBParameter("page_path", pagePath));
        }

        var inner = $"""
            SELECT payload, page_path FROM ({UnionAll(eventTables, "payload, project_key, event_type, page_path, ts")})
            WHERE project_key = $project_key AND event_type = 'click' AND ts >= $from AND ts < $to AND payload IS NOT NULL {pathFilter}
            """;
        var result = new RumHeatmapAggregates();

        // 点击总数（过滤口径与栅格一致）
        result.TotalClicks = await _connectionManager.QueryAsync($"SELECT count(*) FROM ({inner}) WHERE json_extract(payload, '$.x') IS NOT NULL OR json_extract(payload, '$.pageX') IS NOT NULL", parameters, async reader =>
        {
            await reader.ReadAsync(cancellationToken);
            return reader.GetInt64(0);
        }, cancellationToken);

        // 栅格分桶
        var sql = $"""
            SELECT gx, gy, count(*) AS count FROM (
                SELECT least({grid - 1}, floor({grid} * coalesce(try_cast(json_extract(payload, '$.pageX') AS DOUBLE), try_cast(json_extract(payload, '$.x') AS DOUBLE)) / nullif(coalesce(try_cast(json_extract(payload, '$.docW') AS DOUBLE), try_cast(json_extract(payload, '$.pageW') AS DOUBLE)), 0))) AS gx,
                       least({grid - 1}, floor({grid} * coalesce(try_cast(json_extract(payload, '$.pageY') AS DOUBLE), try_cast(json_extract(payload, '$.y') AS DOUBLE)) / nullif(coalesce(try_cast(json_extract(payload, '$.docH') AS DOUBLE), try_cast(json_extract(payload, '$.pageH') AS DOUBLE)), 0))) AS gy
                FROM ({inner})
                WHERE (json_extract(payload, '$.x') IS NOT NULL AND json_extract(payload, '$.pageW') IS NOT NULL)
                    OR (json_extract(payload, '$.pageX') IS NOT NULL AND json_extract(payload, '$.docW') IS NOT NULL)
            ) WHERE gx IS NOT NULL AND gy IS NOT NULL
            GROUP BY gx, gy ORDER BY count DESC
            """;
        var cells = new List<RumHeatmapCell>();
        await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                cells.Add(new RumHeatmapCell { X = Convert.ToInt32(reader.GetDouble(0)), Y = Convert.ToInt32(reader.GetDouble(1)), Count = reader.GetInt64(2) });
            }

            return 0;
        }, cancellationToken);
        result.Cells = cells;

        // 点击目标 TopN（selector 优先，回退 targetId；无目标信息的点击不计）。
        // 注意：必须用 json_extract_string（返回 VARCHAR）——json_extract 返回 JSON 逻辑类型，
        // 与 nullif 的 '' 字面量统一类型时隐式 CAST('' AS JSON) 会转换报错（DuckDB 1.5 实测）
        var targetSql = $"""
            SELECT coalesce(nullif(json_extract_string(payload, '$.selector'), ''), nullif(json_extract_string(payload, '$.targetId'), '')) AS target, count(*) AS count
            FROM ({inner})
            WHERE nullif(json_extract_string(payload, '$.selector'), '') IS NOT NULL OR nullif(json_extract_string(payload, '$.targetId'), '') IS NOT NULL
            GROUP BY 1 ORDER BY count DESC, target LIMIT $top_n
            """;
        var targets = new List<RumClickTarget>();
        await _connectionManager.QueryAsync(targetSql, parameters, async reader =>
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                targets.Add(new RumClickTarget { Target = reader.GetString(0), Count = reader.GetInt64(1) });
            }

            return 0;
        }, cancellationToken);
        result.TopTargets = targets;

        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RumPageNode>> AggregatePageTreeAsync(string projectKey, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var eventTables = await ResolveSourcesAsync(DataDomain.RumEvent, TimeTables.RumEventPrefix, from, to, cancellationToken);
        if (eventTables.Count == 0)
        {
            return [];
        }

        var sql = $"""
            SELECT page_path,
                   count(*) FILTER (event_type = 'click') AS clicks,
                   count(*) FILTER (event_type = 'error') AS errors
            FROM ({UnionAll(eventTables, "page_path, project_key, event_type, ts")})
            WHERE project_key = $project_key AND ts >= $from AND ts < $to AND page_path IS NOT NULL
            GROUP BY page_path ORDER BY clicks DESC, errors DESC LIMIT $limit
            """;
        var parameters = new[] { new DuckDBParameter("project_key", projectKey), new DuckDBParameter("limit", limit), new DuckDBParameter("from", LogStore.ToDb(from)), new DuckDBParameter("to", LogStore.ToDb(to)) };
        var nodes = new List<RumPageNode>();
        await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                nodes.Add(new RumPageNode { PagePath = reader.GetString(0), Clicks = reader.GetInt64(1), Errors = reader.GetInt64(2) });
            }

            return 0;
        }, cancellationToken);
        return nodes;
    }

    private async Task<IReadOnlyList<string>> ResolveSourcesAsync(DataDomain domain, string prefix, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        if (_tableResolver == null)
        {
            return _connectionManager.GetDayTables(prefix, from, to);
        }

        var resolved = await _tableResolver.ResolveAsync(domain, from, to, cancellationToken: token);
        var sources = resolved.LocalTables.ToList();
        sources.AddRange(resolved.ColdFiles.Concat(resolved.ColdParquetGlobs)
            .Select(path => $"read_parquet('{path.Replace("'", "''")}', union_by_name=true)"));
        return sources;
    }

    private static string UnionAll(IReadOnlyList<string> tables, string projection)
    {
        return string.Join(" UNION ALL ", tables.Select(t => $"SELECT {projection} FROM {t}"));
    }
}
