using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SData.Database;
using SData.Infra.Exceptions;
using SData.Metrics;
using SData.Store;
using SData.Store.Models;
using SData.Store.QueryDsl;

namespace SData.Dashboard;

/// <summary>
/// 面板查询执行器：按 query.kind 分派到既有执行器（MetricStore / QueryDsl / PromQL / Analytics）.
/// 全部参数化执行（契约 C1/C2），本类不组装任何 SQL 文本.
/// </summary>
public class PanelQueryExecutor
{
    private static readonly Dictionary<string, MetricAggregation> Aggregations = new(StringComparer.Ordinal)
    {
        ["avg"] = MetricAggregation.Avg,
        ["sum"] = MetricAggregation.Sum,
        ["max"] = MetricAggregation.Max,
        ["min"] = MetricAggregation.Min,
        ["count"] = MetricAggregation.Count,
        ["last"] = MetricAggregation.Last,
        ["p95"] = MetricAggregation.P95,
    };

    private static readonly Dictionary<string, DataDomain> Domains = new(StringComparer.Ordinal)
    {
        ["logs"] = DataDomain.Logs,
        ["traces"] = DataDomain.Traces,
        ["metrics"] = DataDomain.Metrics,
        ["iot"] = DataDomain.Iot,
    };

    private readonly IMetricStore _metricStore;
    private readonly IQueryDslExecutor _dslExecutor;
    private readonly IAnalyticsExecutor _analyticsExecutor;
    private readonly IPromqlExecutor _promqlExecutor;
    private readonly DatabaseContext _databaseContext;
    private readonly ILogger<PanelQueryExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelQueryExecutor"/> class.
    /// </summary>
    public PanelQueryExecutor(
        IMetricStore metricStore,
        IQueryDslExecutor dslExecutor,
        IAnalyticsExecutor analyticsExecutor,
        IPromqlExecutor promqlExecutor,
        DatabaseContext databaseContext,
        ILogger<PanelQueryExecutor> logger)
    {
        _metricStore = metricStore;
        _dslExecutor = dslExecutor;
        _analyticsExecutor = analyticsExecutor;
        _promqlExecutor = promqlExecutor;
        _databaseContext = databaseContext;
        _logger = logger;
    }

    /// <summary>
    /// 执行单个面板查询（错误转为 PanelData.Error，不中断其它面板）.
    /// </summary>
    /// <param name="panel">面板配置.</param>
    /// <param name="from">起始（含）.</param>
    /// <param name="to">结束（不含）.</param>
    /// <param name="rowLimit">行数上限（表格形态）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>面板数据.</returns>
    public async Task<PanelData> ExecuteAsync(PanelConfig panel, DateTimeOffset from, DateTimeOffset to, int rowLimit = 500, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var data = new PanelData { Id = panel.Id, From = from, To = to };
        try
        {
            if (panel.Type is PanelTypes.Markdown or PanelTypes.Group)
            {
                return data;
            }

            var query = panel.Query;
            if (query == null)
            {
                // 旧协议：sql / queryId 直接挂在面板根上
                await ExecuteSqlAsync(panel.Sql, panel.QueryId, ResolveRowLimit(panel.RowLimit, rowLimit), from, to, data, cancellationToken);
                return Finish(data, stopwatch);
            }

            switch (query.Kind)
            {
                case PanelQueryKinds.Metric:
                    await ExecuteMetricAsync(query, from, to, data, cancellationToken);
                    break;
                case PanelQueryKinds.Dsl:
                    await ExecuteDslAsync(query, from, to, ResolveRowLimit(null, rowLimit), data, cancellationToken);
                    break;
                case PanelQueryKinds.Promql:
                    await ExecutePromqlAsync(query, from, to, data, cancellationToken);
                    break;
                case PanelQueryKinds.Custom:
                    await new CustomPanelExecutor(_analyticsExecutor).ExecuteAsync(query.Custom!, data, ResolveRowLimit(panel.RowLimit, rowLimit), from, to, cancellationToken);
                    break;
                case PanelQueryKinds.Sql:
                    var parameters = new Dictionary<string, object?>(query.Parameters);
                    if (query.AutoGranularity)
                    {
                        var hours = (to - from).TotalHours;
                        parameters["chart_bucket"] = hours <= 1 ? "1 minute" : hours <= 6 ? "5 minutes"
                            : hours <= 24 ? "15 minutes" : hours <= 48 ? "30 minutes" : hours <= 168 ? "1 hour" : "1 day";
                    }

                    await ExecuteSqlAsync(query.Sql, query.QueryId, ResolveRowLimit(panel.RowLimit, rowLimit), from, to, data, cancellationToken, parameters);
                    break;
                default:
                    throw BusinessException.Throw(400, $"未知面板查询形态：{query.Kind}");
            }
        }
        catch (PromqlParseException exception)
        {
            data.Error = exception.Message;
        }
        catch (BusinessException exception)
        {
            data.Error = exception.Message;
        }
        catch (ArgumentException exception)
        {
            _logger.LogWarning(exception, "Dashboard panel query rejected");
            data.Error = exception.Message;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Dashboard panel query failed");
            data.Error = "查询执行失败";
        }

        return Finish(data, stopwatch);
    }

    private static PanelData Finish(PanelData data, Stopwatch stopwatch)
    {
        data.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return data;
    }

    private static int ResolveRowLimit(int? panelLimit, int fallback)
    {
        return panelLimit.HasValue ? Math.Clamp(panelLimit.Value, 1, 10_000) : fallback;
    }

    private async Task ExecuteMetricAsync(PanelQueryConfig query, DateTimeOffset from, DateTimeOffset to, PanelData data, CancellationToken cancellationToken)
    {
        var response = await _metricStore.QueryRangeAsync(new MetricSeriesQuery
        {
            Name = query.Name,
            Labels = query.Labels,
            Start = from,
            End = to,
            Aggregation = Aggregations.GetValueOrDefault(query.Aggregation, MetricAggregation.Avg),
            StepSeconds = query.StepSeconds,
        }, cancellationToken);

        data.Series = response.SeriesList
            .Take(Math.Clamp(query.MaxSeries, 1, 100))
            .Select(series => new PanelSeries
            {
                Name = series.Name,
                Labels = series.LabelsJson,
                Points = series.Points.Select(point => new PanelSeriesPoint
                {
                    TimestampMs = point.TimestampMs,
                    Value = point.Value,
                }).ToList(),
            }).ToList();
    }

    private async Task ExecuteDslAsync(PanelQueryConfig query, DateTimeOffset from, DateTimeOffset to, int rowLimit, PanelData data, CancellationToken cancellationToken)
    {
        var model = query.Dsl!;
        // 时间统一由仪表盘/请求区间控制，存储态 range 不生效
        model.Range = new QueryRange { From = from, To = to };
        var result = await _dslExecutor.ExecuteAsync(model, Domains.GetValueOrDefault(query.Domain, DataDomain.Logs), cancellationToken);

        // bucket + 单聚合列（可带分组列）→ 时间序列；多聚合或无 bucket → 表格直传
        var aggColumns = query.Dsl!.Aggregations.Count > 0 ? query.Dsl.Aggregations.Count : 1;
        var groupKeys = result.Columns.Skip(1).Take(Math.Max(0, result.Columns.Count - 1 - aggColumns)).ToList();
        if (aggColumns == 1 && result.Columns.Count >= 2 && result.Columns[0] == "bucket" && model.Bucket != null)
        {
            data.Series = BuildBucketSeries(result, groupKeys, Math.Clamp(query.MaxSeries, 1, 100));
        }
        else
        {
            data.Columns = result.Columns;
            data.Rows = result.Rows.Select(row => result.Columns.Select(column => row.GetValueOrDefault(column)).ToList()).ToList();
        }
    }

    private static List<PanelSeries> BuildBucketSeries(SearchDslResponse result, List<string> groupKeys, int maxSeries)
    {
        var valueColumn = result.Columns[^1];
        var byLabels = new Dictionary<string, PanelSeries>(StringComparer.Ordinal);
        foreach (var row in result.Rows)
        {
            var labels = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in groupKeys)
            {
                labels[key] = row.GetValueOrDefault(key);
            }

            var labelsJson = JsonSerializer.Serialize(labels);
            if (!byLabels.TryGetValue(labelsJson, out var series))
            {
                if (byLabels.Count >= maxSeries)
                {
                    continue;
                }

                series = new PanelSeries { Name = valueColumn, Labels = labelsJson };
                byLabels[labelsJson] = series;
            }

            series.Points.Add(new PanelSeriesPoint
            {
                TimestampMs = ToMs(row.GetValueOrDefault("bucket")),
                Value = AsDouble(row.GetValueOrDefault(valueColumn)),
            });
        }

        return byLabels.Values.ToList();
    }

    private static long ToMs(object? value)
    {
        return value switch
        {
            DateTimeOffset dto => dto.ToUnixTimeMilliseconds(),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            long l => l,
            double d => (long)d,
            string s => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUnixTimeMilliseconds() : 0,
            _ => 0,
        };
    }

    private static double? AsDouble(object? value)
    {
        return value switch
        {
            null => null,
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            _ => double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
        };
    }

    private async Task ExecutePromqlAsync(PanelQueryConfig query, DateTimeOffset from, DateTimeOffset to, PanelData data, CancellationToken cancellationToken)
    {
        var step = TimeSpan.FromSeconds(Math.Clamp(query.StepSeconds, 10, 86400));
        var result = await _promqlExecutor.ExecuteAsync(query.Expr, from, to, step, cancellationToken);
        var maxSeries = Math.Clamp(query.MaxSeries, 1, 100);

        var seriesList = new List<PanelSeries>();
        foreach (var series in result.Result)
        {
            if (seriesList.Count >= maxSeries)
            {
                break;
            }

            var name = series.Metric.GetValueOrDefault("__name__", "expr");
            var labels = series.Metric
                .Where(pair => pair.Key != "__name__")
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var points = new List<PanelSeriesPoint>();
            if (series.Values.Count > 0)
            {
                points.AddRange(series.Values.Select(value => new PanelSeriesPoint
                {
                    TimestampMs = value.Time.ToUnixTimeMilliseconds(),
                    Value = value.Value,
                }));
            }
            else if (series.Value.HasValue)
            {
                points.Add(new PanelSeriesPoint
                {
                    TimestampMs = series.Value.Value.Time.ToUnixTimeMilliseconds(),
                    Value = series.Value.Value.Value,
                });
            }

            seriesList.Add(new PanelSeries
            {
                Name = name,
                Labels = JsonSerializer.Serialize(labels),
                Points = points,
            });
        }

        data.Series = seriesList;
    }

    private async Task ExecuteSqlAsync(string? sql, long queryId, int rowLimit, DateTimeOffset from, DateTimeOffset to, PanelData data, CancellationToken cancellationToken, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        if (queryId > 0)
        {
            sql = await _databaseContext.SavedQueries.AsNoTracking()
                .Where(x => x.Id == queryId)
                .Select(x => x.SqlText)
                .FirstOrDefaultAsync(cancellationToken);
            if (sql == null)
            {
                data.Error = "保存的查询不存在";
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            data.Error = "面板未配置查询";
            return;
        }

        // {{logs}} 等表占位符：走统一搜索展开通道（区间感知 + $start/$end 参数绑定）
        AnalyticsQueryResult result;
        if (sql.Contains("{{", StringComparison.Ordinal) || parameters is { Count: > 0 })
        {
            result = await _analyticsExecutor.ExecuteSearchAsync(sql, from, to, rowLimit, parameters, cancellationToken);
        }
        else
        {
            result = await _analyticsExecutor.ExecuteAsync(sql, rowLimit, cancellationToken);
        }

        data.Columns = result.Columns;
        data.Rows = result.Rows;
        data.ElapsedMs = result.ElapsedMs;
        data.Truncated = result.Truncated;
    }
}
