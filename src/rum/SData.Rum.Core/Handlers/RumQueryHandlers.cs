using MediatR;
using SData.Rum.Models;
using SData.Store;

namespace SData.Rum.Handlers;

/// <summary>
/// RUM 性能汇总查询处理（PV/UV + Web Vitals p75，走 IRumStore 参数化聚合）.
/// </summary>
public class QueryRumPerformanceCommandHandler : IRequestHandler<QueryRumPerformanceCommand, RumPerformanceResponse>
{
    private readonly IRumStore _rumStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryRumPerformanceCommandHandler"/> class.
    /// </summary>
    /// <param name="rumStore">RUM 查询存储.</param>
    public QueryRumPerformanceCommandHandler(IRumStore rumStore)
    {
        _rumStore = rumStore;
    }

    /// <inheritdoc/>
    public async Task<RumPerformanceResponse> Handle(QueryRumPerformanceCommand request, CancellationToken cancellationToken)
    {
        var aggregates = await _rumStore.AggregatePerformanceAsync(request.ProjectKey, request.From, request.To, cancellationToken);
        return new RumPerformanceResponse
        {
            PageViews = aggregates.PageViews,
            UniqueVisitors = aggregates.UniqueVisitors,
            VitalP75 = aggregates.VitalP75,
        };
    }
}

/// <summary>
/// RUM JS 错误 Top 查询处理.
/// </summary>
public class QueryRumErrorsCommandHandler : IRequestHandler<QueryRumErrorsCommand, RumErrorsResponse>
{
    private readonly IRumStore _rumStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryRumErrorsCommandHandler"/> class.
    /// </summary>
    /// <param name="rumStore">RUM 查询存储.</param>
    public QueryRumErrorsCommandHandler(IRumStore rumStore)
    {
        _rumStore = rumStore;
    }

    /// <inheritdoc/>
    public async Task<RumErrorsResponse> Handle(QueryRumErrorsCommand request, CancellationToken cancellationToken)
    {
        var items = await _rumStore.AggregateErrorsAsync(request.ProjectKey, request.From, request.To, request.TopN, cancellationToken);
        return new RumErrorsResponse { Items = [.. items] };
    }
}

/// <summary>
/// RUM 会话列表查询处理.
/// </summary>
public class QueryRumSessionsCommandHandler : IRequestHandler<QueryRumSessionsCommand, RumSessionsResponse>
{
    private readonly IRumStore _rumStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryRumSessionsCommandHandler"/> class.
    /// </summary>
    /// <param name="rumStore">RUM 查询存储.</param>
    public QueryRumSessionsCommandHandler(IRumStore rumStore)
    {
        _rumStore = rumStore;
    }

    /// <inheritdoc/>
    public async Task<RumSessionsResponse> Handle(QueryRumSessionsCommand request, CancellationToken cancellationToken)
    {
        var items = await _rumStore.ListSessionsAsync(request.ProjectKey, request.From, request.To, request.Limit, cancellationToken);
        return new RumSessionsResponse { Items = [.. items] };
    }
}

/// <summary>
/// RUM 会话回放查询处理（按 seq 升序返回分段，T614）.
/// </summary>
public class QueryRumReplayCommandHandler : IRequestHandler<QueryRumReplayCommand, RumReplayResponse>
{
    private readonly IRumStore _rumStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryRumReplayCommandHandler"/> class.
    /// </summary>
    /// <param name="rumStore">RUM 查询存储.</param>
    public QueryRumReplayCommandHandler(IRumStore rumStore)
    {
        _rumStore = rumStore;
    }

    /// <inheritdoc/>
    public async Task<RumReplayResponse> Handle(QueryRumReplayCommand request, CancellationToken cancellationToken)
    {
        var chunks = await _rumStore.GetReplayAsync(request.ProjectKey, request.SessionId, request.From, request.To, cancellationToken);
        return new RumReplayResponse { SessionId = request.SessionId, Chunks = [.. chunks] };
    }
}

/// <summary>
/// RUM 点击热力查询处理（T615；T616 文档坐标/页面过滤/目标 TopN）.
/// </summary>
public class QueryRumHeatmapCommandHandler : IRequestHandler<QueryRumHeatmapCommand, RumHeatmapResponse>
{
    private readonly IRumStore _rumStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryRumHeatmapCommandHandler"/> class.
    /// </summary>
    /// <param name="rumStore">RUM 查询存储.</param>
    public QueryRumHeatmapCommandHandler(IRumStore rumStore)
    {
        _rumStore = rumStore;
    }

    /// <inheritdoc/>
    public async Task<RumHeatmapResponse> Handle(QueryRumHeatmapCommand request, CancellationToken cancellationToken)
    {
        var pagePath = string.IsNullOrWhiteSpace(request.PagePath) ? null : request.PagePath;
        var aggregates = await _rumStore.AggregateHeatmapAsync(request.ProjectKey, request.From, request.To, request.GridCells, pagePath, cancellationToken);
        return new RumHeatmapResponse
        {
            Cells = [.. aggregates.Cells],
            TotalClicks = aggregates.TotalClicks,
            TopTargets = [.. aggregates.TopTargets],
        };
    }
}

/// <summary>
/// RUM 站点地图查询处理（页面树聚合）.
/// </summary>
public class QueryRumSitemapCommandHandler : IRequestHandler<QueryRumSitemapCommand, RumSitemapResponse>
{
    private readonly IRumStore _rumStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryRumSitemapCommandHandler"/> class.
    /// </summary>
    /// <param name="rumStore">RUM 查询存储.</param>
    public QueryRumSitemapCommandHandler(IRumStore rumStore)
    {
        _rumStore = rumStore;
    }

    /// <inheritdoc/>
    public async Task<RumSitemapResponse> Handle(QueryRumSitemapCommand request, CancellationToken cancellationToken)
    {
        var pages = await _rumStore.AggregatePageTreeAsync(request.ProjectKey, request.From, request.To, request.Limit, cancellationToken);
        return new RumSitemapResponse { Items = [.. pages] };
    }
}
