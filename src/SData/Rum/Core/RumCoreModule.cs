using Maomi;
using Microsoft.Extensions.DependencyInjection;
using SData.Rum.Api;
using SData.Rum.Services;

namespace SData.Rum;

/// <summary>
/// RUM 模块.
/// </summary>
[InjectModule<SDataRumSharedModule>]
[InjectModule<SDataRumApiModule>]
public class RumCoreModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddSingleton<IRumOriginPolicy, RumOriginPolicy>();
        context.Services.AddSingleton<RumBeaconService>();
        context.Services.AddSingleton<IRumBeaconService>(services => services.GetRequiredService<RumBeaconService>());
    }
}
