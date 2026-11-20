using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SData.Infra;
using SData.Infra.Models;
using SData.Infra.Services;
using SData.Rum.Models;

namespace SData.Rum.Api.Controllers;

/// <summary>
/// RUM 项目管理（GET|POST /api/rum-projects，仅 admin 可写）.
/// </summary>
[ApiController]
[Route("rum-projects")]
[Authorize]
public class RumProjectsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContextProvider _userContextProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="RumProjectsController"/> class.
    /// </summary>
    /// <param name="mediator">mediator.</param>
    /// <param name="userContextProvider">用户上下文.</param>
    public RumProjectsController(IMediator mediator, IUserContextProvider userContextProvider)
    {
        _mediator = mediator;
        _userContextProvider = userContextProvider;
    }

    /// <summary>
    /// RUM 项目列表.
    /// </summary>
    /// <param name="command">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>分页项目.</returns>
    [HttpGet]
    public async Task<PagedResult<RumProjectItem>> Query([FromQuery] QueryRumProjectsCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 保存 RUM 项目（新增/修改，仅 admin）.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>项目 id.</returns>
    [HttpPost]
    [Authorize(Roles = SDataRoles.Admin)]
    public async Task<Infra.Models.Simple<long>> Save([FromBody] SaveRumProjectCommand command, CancellationToken cancellationToken)
    {
        _userContextProvider.SetUserContext(command);
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 删除 RUM 项目（仅 admin）.
    /// </summary>
    /// <param name="id">项目 id.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>空.</returns>
    [HttpDelete("{id}")]
    [Authorize(Roles = SDataRoles.Admin)]
    public async Task<Infra.Models.EmptyCommandResponse> Delete(long id, CancellationToken cancellationToken)
    {
        var command = new DeleteRumProjectCommand { Id = id };
        _userContextProvider.SetUserContext(command);
        return await _mediator.Send(command, cancellationToken);
    }
}
