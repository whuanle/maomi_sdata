using Maomi;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SData.Infra;
using SData.Infra.Api;
using SData.Infra.Configuration;

namespace SData;

/// <summary>
/// JWT 认证（RSA 签名，issuer/audience 为固定服务名 sdata）.
/// </summary>
public class ConfigureAuthorizaModule : IModule
{
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigureAuthorizaModule"/> class.
    /// </summary>
    /// <param name="configuration">配置.</param>
    public ConfigureAuthorizaModule(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        var rsaProvider = new RsaProvider();

        context.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = AppConst.ServiceName,
                    ValidateAudience = true,
                    ValidAudience = AppConst.ServiceName,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new RsaSecurityKey(rsaProvider.GetPrivateKey()),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                };

                // SignalR JS client 无法设置请求头，/hub/* 握手经 query access_token 认证（T616）
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"].FirstOrDefault();
                        var path = context.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hub"))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        context.Services.AddAuthorization();
        context.Services.AddProblemDetails();
        context.Services.AddExceptionHandler<BusinessExceptionHandler>();
    }
}
