using DuckDB.NET.Data;
using SData.Infra.Configuration;
using SData.Store.Models;
using SData.Store.QueryDsl;

namespace SData.Store;

/// <summary>
/// 日志存储实现（统一查询管线：经 TableResolver 解析本地天表 + 冷层，GET /logs 与 QueryDsl 同源）.
/// </summary>
public class LogStore : ILogStore
{
    /// <summary>GET /logs 投影（不含行级 id，响应模型不暴露）.</summary>
    private const string Projection = "ts, service, severity, body, trace_id, span_id, attributes, resource, source";

    private readonly DuckDbConnectionManager _connectionManager;
    private readonly DuckDbOptions _options;
    private readonly ITableResolver _tableResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogStore"/> class.
    /// </summary>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="systemOptions">系统配置.</param>
    /// <param name="tableResolver">表解析器（统一查询管线）.</param>
    public LogStore(DuckDbConnectionManager connectionManager, SystemOptions systemOptions, ITableResolver tableResolver)
    {
        _connectionManager = connectionManager;
        _options = systemOptions.DuckDb;
        _tableResolver = tableResolver;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LogRecord>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var (start, end) = NormalizeRange(query.Start, query.End, TimeSpan.FromHours(1));
        var resolved = await _tableResolver.ResolveAsync(SData.Store.QueryDsl.DataDomain.Logs, start, end, query.Search, cancellationToken);
        var union = QueryDsl.QueryDslCompiler.BuildBranchUnion(resolved, Projection);
        if (union.Length == 0)
        {
            return Array.Empty<LogRecord>();
        }

        var parameters = new List<DuckDBParameter>
        {
            new("start", ToDb(start)),
            new("end", ToDb(end)),
            new("limit", Math.Clamp(query.Limit, 1, 1000)),
            new("offset", Math.Max(0, query.Offset)),
        };

        var where = new List<string>();
        if (!string.IsNullOrEmpty(query.Service))
        {
            parameters.Add(new DuckDBParameter("service", query.Service));
            where.Add("service = $service");
        }

        if (!string.IsNullOrEmpty(query.Source))
        {
            parameters.Add(new DuckDBParameter("source", query.Source));
            where.Add("source = $source");
        }

        if (!string.IsNullOrEmpty(query.Severity))
        {
            var severities = query.Severity.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var names = new List<string>();
            for (var i = 0; i < severities.Length; i++)
            {
                parameters.Add(new DuckDBParameter($"sev{i}", severities[i].ToUpperInvariant()));
                names.Add($"$sev{i}");
            }

            where.Add($"upper(severity) IN ({string.Join(", ", names)})");
        }

        if (!string.IsNullOrEmpty(query.Search))
        {
            parameters.Add(new DuckDBParameter("search", $"%{query.Search}%"));
            where.Add("body LIKE $search");
        }

        var whereClause = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty;
        var sql = $"SELECT ts, service, severity, body, trace_id, span_id, attributes, resource, source FROM ({union}){whereClause} ORDER BY ts DESC LIMIT $limit OFFSET $offset";

        return await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            var list = new List<LogRecord>();
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(new LogRecord
                {
                    Timestamp = FromDb(reader, 0),
                    Service = reader.GetString(1),
                    Severity = reader.GetString(2),
                    Body = reader.GetString(3),
                    TraceId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    SpanId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    AttributesJson = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ResourceJson = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Source = reader.IsDBNull(8) ? null : reader.GetString(8),
                });
            }

            return (IReadOnlyList<LogRecord>)list;
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var tables = _connectionManager.GetManagedTables(TimeTables.LogsPrefix).TakeLast(3).ToList();
        if (tables.Count == 0)
        {
            return Array.Empty<string>();
        }

        var now = DateTimeOffset.UtcNow;
        var parameters = new List<DuckDBParameter>
        {
            new("start", ToDb(now - TimeSpan.FromDays(3))),
            new("end", ToDb(now + TimeSpan.FromHours(1))),
        };

        var union = BuildUnion(tables, "DISTINCT service", parameters);
        var sql = $"SELECT DISTINCT service FROM ({union}) ORDER BY service";
        return await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            var list = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(reader.GetString(0));
            }

            return (IReadOnlyList<string>)list;
        }, cancellationToken);
    }

    internal static string BuildUnion(IReadOnlyList<string> tables, string select, List<DuckDBParameter>? parameters)
    {
        var branches = tables.Select(table => $"SELECT {select} FROM {table} WHERE ts >= $start AND ts < $end");
        return string.Join(" UNION ALL ", branches);
    }

    internal static (DateTimeOffset Start, DateTimeOffset End) NormalizeRange(DateTimeOffset start, DateTimeOffset end, TimeSpan fallbackSpan)
    {
        var now = DateTimeOffset.UtcNow;
        if (end == default || end > now + TimeSpan.FromDays(1))
        {
            // 缺省查到当前（留 5 分钟余量容纳时钟偏差）
            end = now.AddMinutes(5);
        }

        if (start == default || start >= end)
        {
            start = end - fallbackSpan;
        }

        // 限制最大查询跨度
        return (start, end);
    }

    internal static DateTime ToDb(DateTimeOffset time) => time.ToUniversalTime().UtcDateTime;

    internal static DateTimeOffset FromDb(DbDataReader reader, int index)
    {
        var dateTime = reader.GetDateTime(index);
        return dateTime.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(dateTime, TimeSpan.Zero)
            : new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc), TimeSpan.Zero);
    }
}
