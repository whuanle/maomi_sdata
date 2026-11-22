using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SData.Store.QueryDsl;

namespace SData.Metrics.Api;

/// <summary>
/// 指标查询接口.
/// </summary>
[ApiController]
[Route("metrics")]
[Authorize]
public class MetricsController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetricsController"/> class.
    /// </summary>
    /// <param name="mediator">mediator.</param>
    public MetricsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// 指标名列表.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>指标名.</returns>
    [HttpGet]
    public async Task<List<string>> Names(CancellationToken cancellationToken)
    {
        return await _mediator.Send(new QueryMetricNamesCommand(), cancellationToken);
    }

    /// <summary>
    /// 序列区间查询.
    /// </summary>
    /// <param name="command">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>序列.</returns>
    [HttpPost("query")]
    public async Task<QueryMetricSeriesCommandResponse> Query([FromBody] QueryMetricSeriesCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 统一查询（契约 C1 DSL）：请求体为 range/filters/groupBy/aggregations/bucket/sort/limit.
    /// </summary>
    /// <param name="command">DSL 查询.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>列式查询结果.</returns>
    [HttpPost("search")]
    public async Task<SearchDslResponse> Search([FromBody] SearchMetricsCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }
}
