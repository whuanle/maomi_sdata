using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SData.Infra;
using SData.Infra.Services;

namespace SData.Analytics.Api;

/// <summary>
/// 分析查询接口.
/// </summary>
[ApiController]
[Route("analytics")]
[Authorize]
public class AnalyticsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContextProvider _userContextProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalyticsController"/> class.
    /// </summary>
    /// <param name="mediator">mediator.</param>
    /// <param name="userContextProvider">用户上下文.</param>
    public AnalyticsController(IMediator mediator, IUserContextProvider userContextProvider)
    {
        _mediator = mediator;
        _userContextProvider = userContextProvider;
    }

    /// <summary>读取当前 DuckDB 表及字段目录.</summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>实际表结构.</returns>
    [HttpGet("tables")]
    public async Task<IReadOnlyList<global::SData.Store.AnalyticsTableItem>> Tables(CancellationToken cancellationToken)
    {
        return await _mediator.Send(new QueryAnalyticsTablesCommand(), cancellationToken);
    }

    /// <summary>执行只读分析查询.</summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>查询结果.</returns>
    [HttpPost("query")]
    public async Task<AnalyticsQueryResponse> Query([FromBody] ExecuteQueryCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 统一搜索 SQL 直查（{{logs}} 表占位符 + $start/$end 时间参数绑定）.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>结果.</returns>
    [HttpPost("search")]
    public async Task<AnalyticsQueryResponse> Search([FromBody] ExecuteSearchSqlCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 保存的查询列表.
    /// </summary>
    /// <param name="command">分页.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>分页.</returns>
    [HttpGet("queries")]
    public async Task<Infra.Models.PagedResult<SavedQueryItem>> Queries([FromQuery] QuerySavedQueriesCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 按 id 查询保存项.
    /// </summary>
    /// <param name="id">id.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>保存项.</returns>
    [HttpGet("queries/{id}")]
    public async Task<SavedQueryItem> GetQuery(long id, CancellationToken cancellationToken)
    {
        return await _mediator.Send(new GetSavedQueryCommand { Id = id }, cancellationToken);
    }

    /// <summary>
    /// 保存查询.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>id.</returns>
    [HttpPost("queries")]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public async Task<Infra.Models.Simple<long>> Save([FromBody] SaveQueryCommand command, CancellationToken cancellationToken)
    {
        _userContextProvider.SetUserContext(command);
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 删除保存的查询.
    /// </summary>
    /// <param name="id">id.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>空.</returns>
    [HttpDelete("queries/{id}")]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public async Task<Infra.Models.EmptyCommandResponse> Delete(long id, CancellationToken cancellationToken)
    {
        var command = new DeleteSavedQueryCommand { Id = id };
        _userContextProvider.SetUserContext(command);
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 执行保存的查询.
    /// </summary>
    /// <param name="id">id.</param>
    /// <param name="rowLimit">行数上限.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>结果.</returns>
    [HttpPost("queries/{id}/execute")]
    public async Task<AnalyticsQueryResponse> Execute(long id, [FromQuery] int rowLimit = 1000, CancellationToken cancellationToken = default)
    {
        return await _mediator.Send(new ExecuteSavedQueryCommand { Id = id, RowLimit = rowLimit }, cancellationToken);
    }
}
