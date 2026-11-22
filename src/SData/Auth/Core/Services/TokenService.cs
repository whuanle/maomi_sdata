using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Settings;

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
    private readonly ISystemSettingsProvider _settings;
    private readonly IRsaProvider _rsaProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenService"/> class.
    /// </summary>
    /// <param name="settings">系统设置（token 有效期取运行参数，仅影响之后签发的 token）.</param>
    /// <param name="rsaProvider">RSA 密钥.</param>
    public TokenService(ISystemSettingsProvider settings, IRsaProvider rsaProvider)
    {
        _settings = settings;
        _rsaProvider = rsaProvider;
    }

    /// <inheritdoc/>
    public (string Token, DateTimeOffset ExpireTime) CreateToken(long userId, string username, string role)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(_settings.GetRuntime().Auth.TokenExpireMinutes);
        var claims = new List<Claim>
        {
            new("userid", userId.ToString()),
            new("username", username),
            new(ClaimTypes.Role, role),
        };

        var credentials = new SigningCredentials(new RsaSecurityKey(_rsaProvider.GetPrivateKey()), SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: AppConst.ServiceName,
            audience: AppConst.ServiceName,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: expires.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
