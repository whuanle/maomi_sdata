using Mediator;
using SData.Store;

namespace SData.Traces.Handlers;

/// <summary>
/// trace 列表处理.
/// </summary>
public class QueryTracesCommandHandler : IRequestHandler<QueryTracesCommand, List<TraceSummaryItem>>
{
    private readonly ITraceStore _traceStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryTracesCommandHandler"/> class.
    /// </summary>
    /// <param name="traceStore">链路存储.</param>
    public QueryTracesCommandHandler(ITraceStore traceStore)
    {
        _traceStore = traceStore;
    }

    /// <inheritdoc/>
    public async ValueTask<List<TraceSummaryItem>> Handle(QueryTracesCommand request, CancellationToken cancellationToken)
    {
        var summaries = await _traceStore.QueryAsync(new Store.Models.TraceQuery
        {
            Start = request.Start ?? default,
            End = request.End ?? default,
            Service = request.Service,
            MinDurationUs = request.MinDurationMs * 1000,
            HasError = request.HasError,
            Limit = request.Limit,
        }, cancellationToken);

        return summaries.Select(summary => new TraceSummaryItem
        {
            TraceId = summary.TraceId,
            Service = summary.Service,
            RootName = summary.RootName,
            SpanCount = summary.SpanCount,
            ErrorCount = summary.ErrorCount,
            Start = summary.Start,
            DurationUs = summary.DurationUs,
        }).ToList();
    }
}

/// <summary>
/// 完整 trace 处理.
/// </summary>
public class GetTraceCommandHandler : IRequestHandler<GetTraceCommand, List<SpanItem>>
{
    private readonly ITraceStore _traceStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="GetTraceCommandHandler"/> class.
    /// </summary>
    /// <param name="traceStore">链路存储.</param>
    public GetTraceCommandHandler(ITraceStore traceStore)
    {
        _traceStore = traceStore;
    }

    /// <inheritdoc/>
    public async ValueTask<List<SpanItem>> Handle(GetTraceCommand request, CancellationToken cancellationToken)
    {
        var spans = await _traceStore.GetTraceAsync(request.TraceId, cancellationToken);
        return spans.Select(span => new SpanItem
        {
            TraceId = span.TraceId,
            SpanId = span.SpanId,
            ParentSpanId = span.ParentSpanId,
            Timestamp = span.Timestamp,
            DurationUs = span.DurationUs,
            Service = span.Service,
            Name = span.Name,
            Kind = span.Kind,
            Status = span.Status,
            Attributes = span.AttributesJson,
            Events = span.EventsJson,
            Resource = span.ResourceJson,
        }).ToList();
    }
}

/// <summary>
/// 链路统一查询处理（DSL，MaxQueryDays 约束在执行器内）.
/// </summary>
public class SearchTracesCommandHandler : IRequestHandler<SearchTracesCommand, SData.Store.QueryDsl.SearchDslResponse>
{
    private readonly SData.Store.QueryDsl.IQueryDslExecutor _executor;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchTracesCommandHandler"/> class.
    /// </summary>
    /// <param name="executor">DSL 执行器.</param>
    public SearchTracesCommandHandler(SData.Store.QueryDsl.IQueryDslExecutor executor)
    {
        _executor = executor;
    }

    /// <inheritdoc/>
    public async ValueTask<SData.Store.QueryDsl.SearchDslResponse> Handle(SearchTracesCommand request, CancellationToken cancellationToken)
    {
        return await _executor.ExecuteAsync(request, SData.Store.QueryDsl.DataDomain.Traces, cancellationToken);
    }
}
