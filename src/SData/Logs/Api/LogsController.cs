using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SData.Store.QueryDsl;

namespace SData.Logs.Api;

/// <summary>
/// 日志查询接口.
/// </summary>
[ApiController]
[Route("logs")]
[Authorize]
public class LogsController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogsController"/> class.
    /// </summary>
    /// <param name="mediator">mediator.</param>
    public LogsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// 日志查询.
    /// </summary>
    /// <param name="command">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>分页日志.</returns>
    [HttpGet]
    public async Task<Infra.Models.PagedResult<LogItem>> Query([FromQuery] QueryLogsCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 服务名列表.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>服务名.</returns>
    [HttpGet("services")]
    public async Task<List<string>> Services(CancellationToken cancellationToken)
    {
        return await _mediator.Send(new QueryLogServicesCommand(), cancellationToken);
    }

    /// <summary>
    /// 统一查询（契约 C1 DSL）：请求体为 range/filters/groupBy/aggregations/bucket/sort/limit.
    /// </summary>
    /// <param name="command">DSL 查询.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>列式查询结果.</returns>
    [HttpPost("search")]
    public async Task<SearchDslResponse> Search([FromBody] SearchLogsCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }
}
