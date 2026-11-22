using System.Security.Claims;

namespace SData.Infra.Services;

/// <summary>
/// 当前请求用户上下文.
/// </summary>
public interface IUserContext
{
    /// <summary>
    /// 是否已认证.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// 用户 id.
    /// </summary>
    long UserId { get; }

    /// <summary>
    /// 用户名.
    /// </summary>
    string UserName { get; }

    /// <summary>
    /// 角色（admin/user）.
    /// </summary>
    string Role { get; }

    /// <summary>
    /// 构建 ClaimsPrincipal.
    /// </summary>
    ClaimsPrincipal BuildPrincipal();
}
