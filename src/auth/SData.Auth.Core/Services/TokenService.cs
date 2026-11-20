using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using SData.Infra.Configuration;

namespace SData.Auth.Services;

/// <summary>
/// JWT token 服务（RSA 签名）.
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// 为用户生成 token.
    /// </summary>
    /// <param name="userId">用户 id.</param>
    /// <param name="username">用户名.</param>
    /// <param name="role">角色.</param>
    /// <returns>(token, 过期时间).</returns>
    (string Token, DateTimeOffset ExpireTime) CreateToken(long userId, string username, string role);
}

/// <summary>
/// ITokenService 实现.
/// </summary>
public class TokenService : ITokenService
{
    private readonly SystemOptions _systemOptions;
    private readonly IRsaProvider _rsaProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenService"/> class.
    /// </summary>
    /// <param name="systemOptions">系统配置.</param>
    /// <param name="rsaProvider">RSA 密钥.</param>
    public TokenService(SystemOptions systemOptions, IRsaProvider rsaProvider)
    {
        _systemOptions = systemOptions;
        _rsaProvider = rsaProvider;
    }

    /// <inheritdoc/>
    public (string Token, DateTimeOffset ExpireTime) CreateToken(long userId, string username, string role)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(_systemOptions.Auth.TokenExpireMinutes);
        var claims = new List<Claim>
        {
            new("userid", userId.ToString()),
            new("username", username),
            new(ClaimTypes.Role, role),
        };

        var credentials = new SigningCredentials(new RsaSecurityKey(_rsaProvider.GetPrivateKey()), SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: _systemOptions.Server,
            audience: _systemOptions.Server,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: expires.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
