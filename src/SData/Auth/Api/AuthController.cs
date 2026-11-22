using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SData.Infra;
using SData.Infra.Services;

namespace SData.Auth.Api;

/// <summary>
/// 认证接口.
/// </summary>
[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContextProvider _userContextProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthController"/> class.
    /// </summary>
    /// <param name="mediator"> mediator.</param>
    /// <param name="userContextProvider">用户上下文.</param>
    public AuthController(IMediator mediator, IUserContextProvider userContextProvider)
    {
        _mediator = mediator;
        _userContextProvider = userContextProvider;
    }

    /// <summary>
    /// 登录.
    /// </summary>
    /// <param name="command">登录请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>token.</returns>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<LoginCommandResponse> Login([FromBody] LoginCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 修改当前用户密码.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>空.</returns>
    [HttpPost("password/change")]
    [Authorize]
    public async Task<Infra.Models.EmptyCommandResponse> ChangePassword([FromBody] ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        _userContextProvider.SetUserContext(command);
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 当前用户信息.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>用户信息.</returns>
    [HttpGet("me")]
    [Authorize]
    public async Task<CurrentUserModel> Me(CancellationToken cancellationToken)
    {
        var command = new QueryCurrentUserCommand();
        _userContextProvider.SetUserContext(command);
        return await _mediator.Send(command, cancellationToken);
    }
}

/// <summary>
/// 用户管理接口（列表登录即可见，团队共用；管理操作仅超级管理员）.
/// </summary>
[ApiController]
[Route("users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContextProvider _userContextProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="UsersController"/> class.
    /// </summary>
    /// <param name="mediator">mediator.</param>
    /// <param name="userContextProvider">用户上下文.</param>
    public UsersController(IMediator mediator, IUserContextProvider userContextProvider)
    {
        _mediator = mediator;
        _userContextProvider = userContextProvider;
    }

    /// <summary>
    /// 用户列表.
    /// </summary>
    /// <param name="command">分页参数.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>分页.</returns>
    [HttpGet]
    public async Task<Infra.Models.PagedResult<UserItem>> Query([FromQuery] QueryUsersCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 创建用户（仅超级管理员）.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>用户 id.</returns>
    [HttpPost]
    [Authorize(Roles = SDataRoles.Admin)]
    public async Task<Infra.Models.Simple<long>> Create([FromBody] CreateUserCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 删除用户.
    /// </summary>
    /// <param name="id">用户 id.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>空.</returns>
    [HttpDelete("{id}")]
    [Authorize(Roles = SDataRoles.Admin)]
    public async Task<Infra.Models.EmptyCommandResponse> Delete(long id, CancellationToken cancellationToken)
    {
        var command = new DeleteUserCommand { UserId = id };
        _userContextProvider.SetUserContext(command);
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 重置用户密码.
    /// </summary>
    /// <param name="id">用户 id.</param>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>空.</returns>
    [HttpPost("{id}/password/reset")]
    [Authorize(Roles = SDataRoles.Admin)]
    public async Task<Infra.Models.EmptyCommandResponse> ResetPassword(long id, [FromBody] ResetUserPasswordCommand command, CancellationToken cancellationToken)
    {
        command.UserId = id;
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 修改用户权限.
    /// </summary>
    /// <param name="id">用户 id.</param>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>空.</returns>
    [HttpPost("{id}/role")]
    [Authorize(Roles = SDataRoles.Admin)]
    public async Task<Infra.Models.EmptyCommandResponse> UpdateRole(long id, [FromBody] UpdateUserRoleCommand command, CancellationToken cancellationToken)
    {
        command.UserId = id;
        return await _mediator.Send(command, cancellationToken);
    }
}
