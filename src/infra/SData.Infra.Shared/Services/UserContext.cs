using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace SData.Infra.Services;

/// <summary>
/// 基于 JWT claims 的用户上下文（ASP.NET Core 请求内有效）.
/// </summary>
public class UserContext : IUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserContext"/> class.
    /// </summary>
    /// <param name="httpContextAccessor">HttpContext 访问器.</param>
    public UserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    /// <inheritdoc/>
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    /// <inheritdoc/>
    public long UserId => GetClaimLong("userid");

    /// <inheritdoc/>
    public string UserName => Principal?.FindFirstValue("username") ?? string.Empty;

    /// <inheritdoc/>
    public string Role => Principal?.FindFirstValue(ClaimTypes.Role) ?? string.Empty;

    /// <inheritdoc/>
    public ClaimsPrincipal BuildPrincipal()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("userid", UserId.ToString()),
            new Claim("username", UserName),
            new Claim(ClaimTypes.Role, Role),
        }, authenticationType: "sdata");

        return new ClaimsPrincipal(identity);
    }

    private long GetClaimLong(string name)
    {
        var value = Principal?.FindFirstValue(name);
        return long.TryParse(value, out var id) ? id : 0;
    }
}
