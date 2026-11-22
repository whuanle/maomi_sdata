using Mediator;
using SData.Infra.Models;
using SData.Infra.Services;

namespace SData.Auth;

/// <summary>
/// 登录请求.
/// </summary>
public class LoginCommand : IRequest<LoginCommandResponse>, IModelValidator<LoginCommand>
{
    /// <summary>
    /// 用户名.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 密码.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<LoginCommand> validate)
    {
        validate.RuleFor(x => x.Username).NotEmpty().MaximumLength(64);
        validate.RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

/// <summary>
/// 登录响应.
/// </summary>
public class LoginCommandResponse
{
    /// <summary>
    /// JWT token.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// 过期时间.
    /// </summary>
    public DateTimeOffset ExpireTime { get; set; }

    /// <summary>
    /// 用户 id.
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// 用户名.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// 角色.
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// RSA 公钥（前端密码加密用）.
    /// </summary>
    public string RsaPublic { get; set; } = string.Empty;
}
