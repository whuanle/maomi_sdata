using Maomi;
using Microsoft.Extensions.DependencyInjection;
using SData.Rum.Api.Services;

namespace SData.Rum.Api;

/// <summary>
/// SDataRumApiModule.
/// </summary>
public class SDataRumApiModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddSingleton<IRumLivePublisher, HubRumLivePublisher>();
    }
}
