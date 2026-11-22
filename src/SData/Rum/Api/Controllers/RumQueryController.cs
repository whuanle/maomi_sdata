using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SData.Rum.Models;

namespace SData.Rum.Api.Controllers;

/// <summary>
/// RUM 查询面（性能汇总/JS 错误 Top/会话列表，基于 rum_* 天表聚合；T613）.
/// </summary>
[ApiController]
[Route("rum")]
[Authorize]
public class RumQueryController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>
    /// Initializes a new instance of the <see cref="RumQueryController"/> class.
    /// </summary>
    /// <param name="mediator">mediator.</param>
    public RumQueryController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// 性能汇总：PV/UV + Web Vitals p75.
    /// </summary>
    /// <param name="command">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>性能汇总.</returns>
    [HttpGet("performance")]
    public async Task<RumPerformanceResponse> Performance([FromQuery] QueryRumPerformanceCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// JS 错误 Top N.
    /// </summary>
    /// <param name="command">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>错误聚合.</returns>
    [HttpGet("errors")]
    public async Task<RumErrorsResponse> Errors([FromQuery] QueryRumErrorsCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 会话列表（按最近活跃降序）.
    /// </summary>
    /// <param name="command">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>会话摘要列表.</returns>
    /// <summary>分页会话检索.</summary>
    [HttpGet("sessions/search")]
    public ValueTask<SData.Store.RumSessionPage> SearchSessions([FromQuery] SearchRumSessionsCommand command, CancellationToken cancellationToken)
    {
        return _mediator.Send(command, cancellationToken);
    }

    /// <summary>会话事件时间线（最多 1000 条）.</summary>
    [HttpGet("sessions/events")]
    public ValueTask<IReadOnlyList<SData.Store.RumSessionEvent>> SessionEvents([FromQuery] QueryRumSessionEventsCommand command, CancellationToken cancellationToken)
    {
        return _mediator.Send(command, cancellationToken);
    }

    [HttpGet("sessions")]
    public async Task<RumSessionsResponse> Sessions([FromQuery] QueryRumSessionsCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 会话回放分段（C3：按 seq 升序，gzip(rrweb 事件 JSON) 的 base64，T614）.
    /// </summary>
    /// <param name="sessionId">会话标识（路由）.</param>
    /// <param name="from">开始时间（含）.</param>
    /// <param name="to">结束时间（不含）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>回放分段.</returns>
    [HttpGet("v1/replay/{sessionId}")]
    public async Task<RumReplayResponse> Replay(string sessionId, string projectKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        return await _mediator.Send(new QueryRumReplayCommand { ProjectKey = projectKey, SessionId = sessionId, From = from, To = to }, cancellationToken);
    }

    /// <summary>
    /// 点击热力栅格（T615）.
    /// </summary>
    /// <param name="command">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>栅格单元.</returns>
    [HttpGet("heatmap")]
    public async Task<RumHeatmapResponse> Heatmap([FromQuery] QueryRumHeatmapCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 站点地图（页面树聚合，T615）.
    /// </summary>
    /// <param name="command">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>页面节点.</returns>
    [HttpGet("sitemap")]
    public async Task<RumSitemapResponse> Sitemap([FromQuery] QueryRumSitemapCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }
}
