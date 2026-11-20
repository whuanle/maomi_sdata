using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SData.Store.QueryDsl;

namespace SData.Traces.Api;

/// <summary>
/// 链路查询接口.
/// </summary>
[ApiController]
[Route("traces")]
[Authorize]
public class TracesController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>
    /// Initializes a new instance of the <see cref="TracesController"/> class.
    /// </summary>
    /// <param name="mediator">mediator.</param>
    public TracesController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// trace 列表.
    /// </summary>
    /// <param name="command">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>trace 概要.</returns>
    [HttpGet]
    public async Task<List<TraceSummaryItem>> Query([FromQuery] QueryTracesCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 完整 trace（全部 span，用于瀑布图）.
    /// </summary>
    /// <param name="traceId">trace id.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>span 列表.</returns>
    [HttpGet("{traceId}")]
    public async Task<List<SpanItem>> Get(string traceId, CancellationToken cancellationToken)
    {
        return await _mediator.Send(new GetTraceCommand { TraceId = traceId }, cancellationToken);
    }

    /// <summary>
    /// 统一查询（契约 C1 DSL）：请求体为 range/filters/groupBy/aggregations/bucket/sort/limit.
    /// </summary>
    /// <param name="command">DSL 查询.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>列式查询结果.</returns>
    [HttpPost("search")]
    public async Task<SearchDslResponse> Search([FromBody] SearchTracesCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }
}
