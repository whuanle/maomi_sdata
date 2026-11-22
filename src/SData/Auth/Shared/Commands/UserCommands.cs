using Mediator;
using SData.Infra;
using SData.Infra.Models;
using SData.Infra.Services;

namespace SData.Auth;

/// <summary>
/// 修改当前用户密码.
/// </summary>
public class ChangePasswordCommand : IRequest<EmptyCommandResponse>, IUserIdContext, IModelValidator<ChangePasswordCommand>
{
    /// <summary>
    /// 旧密码.
    /// </summary>
    public string OldPassword { get; set; } = string.Empty;

    /// <summary>
    /// 新密码.
    /// </summary>
    public string NewPassword { get; set; } = string.Empty;

    /// <inheritdoc/>
    public long ContextUserId { get; set; }

    /// <inheritdoc/>
    public string ContextUserRole { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<ChangePasswordCommand> validate)
    {
        validate.RuleFor(x => x.OldPassword).NotEmpty().MaximumLength(128);
        validate.RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(128);
    }
}

/// <summary>
/// 当前用户信息.
/// </summary>
public class CurrentUserModel
{
    /// <summary>
    /// 用户 id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 用户名.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 角色.
    /// </summary>
    public string Role { get; set; } = string.Empty;
}

/// <summary>
/// 查询当前用户.
/// </summary>
public class QueryCurrentUserCommand : IRequest<CurrentUserModel>, IUserIdContext
{
    /// <inheritdoc/>
    public long ContextUserId { get; set; }

    /// <inheritdoc/>
    public string ContextUserRole { get; set; } = string.Empty;
}

/// <summary>
/// 创建用户（仅超级管理员）.
/// </summary>
public class CreateUserCommand : IRequest<Simple<long>>, IModelValidator<CreateUserCommand>
{
    /// <summary>
    /// 用户名.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 初始密码.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// 角色：write / readonly（超级管理员由系统种子唯一提供，不允许创建）.
    /// </summary>
    public string Role { get; set; } = SDataRoles.Write;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<CreateUserCommand> validate)
    {
        validate.RuleFor(x => x.Username).NotEmpty().Matches("^[a-zA-Z0-9_.-]{2,64}$");
        validate.RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(128);
        validate.RuleFor(x => x.Role).Must(x => x is SDataRoles.Write or SDataRoles.ReadOnly);
    }
}

/// <summary>
/// 删除用户（仅超级管理员）.
/// </summary>
public class DeleteUserCommand : IRequest<EmptyCommandResponse>, IUserIdContext, IModelValidator<DeleteUserCommand>
{
    /// <summary>
    /// 用户 id.
    /// </summary>
    public long UserId { get; set; }

    /// <inheritdoc/>
    public long ContextUserId { get; set; }

    /// <inheritdoc/>
    public string ContextUserRole { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<DeleteUserCommand> validate)
    {
        validate.RuleFor(x => x.UserId).GreaterThan(0);
    }
}

/// <summary>
/// 重置用户密码（仅超级管理员）.
/// </summary>
public class ResetUserPasswordCommand : IRequest<EmptyCommandResponse>, IModelValidator<ResetUserPasswordCommand>
{
    /// <summary>
    /// 用户 id.
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// 新密码.
    /// </summary>
    public string NewPassword { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<ResetUserPasswordCommand> validate)
    {
        validate.RuleFor(x => x.UserId).GreaterThan(0);
        validate.RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(128);
    }
}

/// <summary>
/// 修改用户权限（仅超级管理员，write / readonly 互换）.
/// </summary>
public class UpdateUserRoleCommand : IRequest<EmptyCommandResponse>, IModelValidator<UpdateUserRoleCommand>
{
    /// <summary>
    /// 用户 id.
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// 目标角色：write / readonly.
    /// </summary>
    public string Role { get; set; } = SDataRoles.Write;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<UpdateUserRoleCommand> validate)
    {
        validate.RuleFor(x => x.UserId).GreaterThan(0);
        validate.RuleFor(x => x.Role).Must(x => x is SDataRoles.Write or SDataRoles.ReadOnly);
    }
}

/// <summary>
/// 用户项.
/// </summary>
public class UserItem
{
    /// <summary>用户 id.</summary>
    public long Id { get; set; }

    /// <summary>用户名.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>角色.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>创建时间.</summary>
    public DateTimeOffset CreateTime { get; set; }
}

/// <summary>
/// 查询用户列表（仅超级管理员）.
/// </summary>
public class QueryUsersCommand : PagedParameter, IRequest<PagedResult<UserItem>>
{
}
