using Maomi;
using Microsoft.Extensions.DependencyInjection;
using SData.Auth.Api;
using SData.Auth.Services;

namespace SData.Auth;

/// <summary>
/// 认证模块.
/// </summary>
[InjectModule<SDataAuthSharedModule>]
[InjectModule<SDataAuthApiModule>]
public class AuthCoreModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddScoped<ITokenService, TokenService>();
    }
}
