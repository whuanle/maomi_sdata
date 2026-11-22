using Mediator;
using SData.Store;
using SData.Store.Models;

namespace SData.Metrics.Handlers;

/// <summary>
/// 指标名列表处理.
/// </summary>
public class QueryMetricNamesCommandHandler : IRequestHandler<QueryMetricNamesCommand, List<string>>
{
    private readonly IMetricStore _metricStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryMetricNamesCommandHandler"/> class.
    /// </summary>
    /// <param name="metricStore">指标存储.</param>
    public QueryMetricNamesCommandHandler(IMetricStore metricStore)
    {
        _metricStore = metricStore;
    }

    /// <inheritdoc/>
    public async ValueTask<List<string>> Handle(QueryMetricNamesCommand request, CancellationToken cancellationToken)
    {
        var names = await _metricStore.ListNamesAsync(cancellationToken);
        return names.ToList();
    }
}

/// <summary>
/// 指标序列查询处理.
/// </summary>
public class QueryMetricSeriesCommandHandler : IRequestHandler<QueryMetricSeriesCommand, QueryMetricSeriesCommandResponse>
{
    private readonly IMetricStore _metricStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryMetricSeriesCommandHandler"/> class.
    /// </summary>
    /// <param name="metricStore">指标存储.</param>
    public QueryMetricSeriesCommandHandler(IMetricStore metricStore)
    {
        _metricStore = metricStore;
    }

    /// <inheritdoc/>
    public async ValueTask<QueryMetricSeriesCommandResponse> Handle(QueryMetricSeriesCommand request, CancellationToken cancellationToken)
    {
        var response = await _metricStore.QueryRangeAsync(new MetricSeriesQuery
        {
            Name = request.Name,
            Labels = request.Labels,
            Start = request.Start ?? default,
            End = request.End ?? default,
            Aggregation = ParseAggregation(request.Aggregation),
            StepSeconds = request.StepSeconds,
        }, cancellationToken);

        return new QueryMetricSeriesCommandResponse
        {
            Series = response.SeriesList.Select(series => new MetricSeriesItem
            {
                Name = series.Name,
                Labels = series.LabelsJson,
                Points = series.Points.Select(point => new MetricPoint
                {
                    TimestampMs = point.TimestampMs,
                    Value = point.Value,
                }).ToList(),
            }).ToList(),
        };
    }

    private static MetricAggregation ParseAggregation(string aggregation)
    {
        return aggregation.ToLowerInvariant() switch
        {
            "sum" => MetricAggregation.Sum,
            "max" => MetricAggregation.Max,
            "min" => MetricAggregation.Min,
            "count" => MetricAggregation.Count,
            "last" => MetricAggregation.Last,
            "p95" => MetricAggregation.P95,
            _ => MetricAggregation.Avg,
        };
    }
}

/// <summary>
/// 指标统一查询处理（DSL，MaxQueryDays 约束在执行器内）.
/// </summary>
public class SearchMetricsCommandHandler : IRequestHandler<SearchMetricsCommand, SData.Store.QueryDsl.SearchDslResponse>
{
    private readonly SData.Store.QueryDsl.IQueryDslExecutor _executor;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchMetricsCommandHandler"/> class.
    /// </summary>
    /// <param name="executor">DSL 执行器.</param>
    public SearchMetricsCommandHandler(SData.Store.QueryDsl.IQueryDslExecutor executor)
    {
        _executor = executor;
    }

    /// <inheritdoc/>
    public async ValueTask<SData.Store.QueryDsl.SearchDslResponse> Handle(SearchMetricsCommand request, CancellationToken cancellationToken)
    {
        return await _executor.ExecuteAsync(request, SData.Store.QueryDsl.DataDomain.Metrics, cancellationToken);
    }
}
