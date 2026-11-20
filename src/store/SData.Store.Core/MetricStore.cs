using DuckDB.NET.Data;
using SData.Infra.Configuration;
using SData.Store.Models;
using SData.Store.QueryDsl;

namespace SData.Store;

/// <summary>
/// 指标存储实现.
/// </summary>
public class MetricStore : IMetricStore
{
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly DuckDbOptions _options;
    private readonly ITableResolver? _tableResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetricStore"/> class.
    /// </summary>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="systemOptions">系统配置.</param>
    public MetricStore(DuckDbConnectionManager connectionManager, SystemOptions systemOptions, ITableResolver? tableResolver = null)
    {
        _connectionManager = connectionManager;
        _options = systemOptions.DuckDb;
        _tableResolver = tableResolver;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var resolved = _tableResolver == null
            ? new ResolvedTables(_connectionManager.GetManagedTables(TimeTables.MetricsPrefix).TakeLast(7).ToList(), [])
            : await _tableResolver.ResolveAsync(DataDomain.Metrics, now.AddDays(-7), now.AddMinutes(5), cancellationToken: cancellationToken);
        var union = QueryDslCompiler.BuildBranchUnion(resolved, "name");
        if (union.Length == 0)
        {
            return Array.Empty<string>();
        }

        var sql = $"SELECT DISTINCT name FROM ({union}) ORDER BY name";
        var parameters = new[] { new DuckDBParameter("start", LogStore.ToDb(now.AddDays(-7))), new DuckDBParameter("end", LogStore.ToDb(now.AddMinutes(5))) };
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

    /// <inheritdoc/>
    public async Task<SeriesResponse> QueryRangeAsync(MetricSeriesQuery query, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var (start, end) = LogStore.NormalizeRange(query.Start, query.End, TimeSpan.FromMinutes(30));
        var spanHours = (end - start).TotalHours;
        if (_tableResolver == null && spanHours > 24 && query.Aggregation is MetricAggregation.Avg or MetricAggregation.Sum or MetricAggregation.Max or MetricAggregation.Min or MetricAggregation.Count)
        {
            // 契约 C6：跨度 > 24h 且聚合可由 rollup 列推导时走汇总表（p95/last 语义 rollup 无法表达，保持原始表）
            return await QueryRollupAsync(query, start, end, cancellationToken);
        }

        var stepSeconds = Math.Clamp(query.StepSeconds, 10, 86400);
        var resolved = _tableResolver == null
            ? new ResolvedTables(_connectionManager.GetDayTables(TimeTables.MetricsPrefix, start, end), [])
            : await _tableResolver.ResolveAsync(DataDomain.Metrics, start, end, cancellationToken: cancellationToken);
        var tables = resolved.LocalTables;
        var response = new SeriesResponse();
        if (tables.Count == 0 && resolved.ColdFiles.Count == 0 && resolved.ColdParquetGlobs.Count == 0)
        {
            return response;
        }

        var parameters = new List<DuckDBParameter>
        {
            new("name", query.Name),
            new("start", LogStore.ToDb(start)),
            new("end", LogStore.ToDb(end)),
            new("stepMs", (long)stepSeconds * 1000),
        };

        var where = new List<string> { "name = $name" };
        var labels = query.Labels.Where(x => !string.IsNullOrEmpty(x.Key)).Take(10).ToList();
        for (var i = 0; i < labels.Count; i++)
        {
            parameters.Add(new DuckDBParameter($"labelPath{i}", $"$.{labels[i].Key}"));
            parameters.Add(new DuckDBParameter($"labelValue{i}", labels[i].Value));
            where.Add($"json_extract_string(labels, $labelPath{i}) = $labelValue{i}");
        }

        var union = QueryDslCompiler.BuildBranchUnion(resolved, "name, labels, ts, value");
        var sql = $"""
            SELECT name, labels, bucket, {AggExpression(query.Aggregation, "value")} AS value FROM (
                SELECT name, labels, (epoch_ms(ts) // $stepMs) * $stepMs AS bucket, value
                FROM ({union})
                WHERE {string.Join(" AND ", where)}
            )
            GROUP BY name, labels, bucket
            ORDER BY bucket
            """;

        return await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            var byLabels = new Dictionary<string, SeriesResponse.Series>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                var labelsJson = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
                var bucket = reader.GetInt64(2);
                var value = reader.IsDBNull(3) ? (double?)null : Convert.ToDouble(reader.GetValue(3));
                var key = name + "|" + labelsJson;
                if (!byLabels.TryGetValue(key, out var series))
                {
                    series = new SeriesResponse.Series { Name = name, LabelsJson = labelsJson };
                    byLabels[key] = series;
                    response.SeriesList.Add(series);
                }

                series.Points.Add(new SeriesPoint { TimestampMs = bucket, Value = value });
            }

            return response;
        }, cancellationToken);
    }

    /// <summary>
    /// 长区间查询（跨度 &gt; 24h）：从 metrics_rollup_1h 汇总表聚合；步长下限 1h（rollup 桶粒度）.
    /// </summary>
    /// <param name="query">序列查询.</param>
    /// <param name="start">起.</param>
    /// <param name="end">止.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>序列响应.</returns>
    internal async Task<SeriesResponse> QueryRollupAsync(MetricSeriesQuery query, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var response = new SeriesResponse();
        var stepSeconds = Math.Max(Math.Clamp(query.StepSeconds, 10, 86400), 3600);
        var parameters = new List<DuckDBParameter>
        {
            new("name", query.Name),
            new("start", LogStore.ToDb(start)),
            new("end", LogStore.ToDb(end)),
            new("stepMs", (long)stepSeconds * 1000),
        };

        var where = new List<string> { "name = $name", "bucket_ts >= $start", "bucket_ts < $end" };
        var labels = query.Labels.Where(x => !string.IsNullOrEmpty(x.Key)).Take(10).ToList();
        for (var i = 0; i < labels.Count; i++)
        {
            parameters.Add(new DuckDBParameter($"labelPath{i}", $"$.{labels[i].Key}"));
            parameters.Add(new DuckDBParameter($"labelValue{i}", labels[i].Value));
            where.Add($"json_extract_string(labels, $labelPath{i}) = $labelValue{i}");
        }

        var aggregation = query.Aggregation switch
        {
            MetricAggregation.Avg => "(sum(\"sum\") / NULLIF(sum(\"count\"), 0))",
            MetricAggregation.Sum => "sum(\"sum\")",
            MetricAggregation.Max => "max(\"max\")",
            MetricAggregation.Min => "min(\"min\")",
            _ => "sum(\"count\")",
        };

        var sql = $"""
            SELECT name, labels, bucket, {aggregation} AS value FROM (
                SELECT name, labels, (epoch_ms(bucket_ts) // $stepMs) * $stepMs AS bucket, "min", "max", "sum", "count"
                FROM {TimeTables.Rollup1hTable}
                WHERE {string.Join(" AND ", where)}
            )
            GROUP BY name, labels, bucket
            ORDER BY bucket
            """;

        return await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            var byLabels = new Dictionary<string, SeriesResponse.Series>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                var labelsJson = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
                var bucket = reader.GetInt64(2);
                var value = reader.IsDBNull(3) ? (double?)null : Convert.ToDouble(reader.GetValue(3));
                var key = name + "|" + labelsJson;
                if (!byLabels.TryGetValue(key, out var series))
                {
                    series = new SeriesResponse.Series { Name = name, LabelsJson = labelsJson };
                    byLabels[key] = series;
                    response.SeriesList.Add(series);
                }

                series.Points.Add(new SeriesPoint { TimestampMs = bucket, Value = value });
            }

            return response;
        }, cancellationToken);
    }

    internal static string AggExpression(MetricAggregation aggregation, string column)
    {
        return aggregation switch
        {
            MetricAggregation.Avg => $"avg({column})",
            MetricAggregation.Sum => $"sum({column})",
            MetricAggregation.Max => $"max({column})",
            MetricAggregation.Min => $"min({column})",
            MetricAggregation.Count => $"count({column})",
            MetricAggregation.Last => $"arg_max({column}, ts)",
            MetricAggregation.P95 => $"quantile_cont({column}, 0.95)",
            _ => $"avg({column})",
        };
    }
}
