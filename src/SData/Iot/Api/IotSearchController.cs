using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SData.Store.QueryDsl;

namespace SData.Iot.Api;

/// <summary>
/// 遥测统一查询接口（契约 C1，POST /api/iot/search）.
/// </summary>
[ApiController]
[Route("iot")]
[Authorize]
public class IotSearchController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>
    /// Initializes a new instance of the <see cref="IotSearchController"/> class.
    /// </summary>
    /// <param name="mediator">mediator.</param>
    public IotSearchController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// 统一查询（契约 C1 DSL）：请求体为 range/filters/groupBy/aggregations/bucket/sort/limit.
    /// </summary>
    /// <param name="command">DSL 查询.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>列式查询结果.</returns>
    [HttpPost("search")]
    public async Task<SearchDslResponse> Search([FromBody] SearchTelemetryCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }
}
