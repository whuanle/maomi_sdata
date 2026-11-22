using Maomi;
using Microsoft.Extensions.DependencyInjection;
using SData.Monitor.Api;

namespace SData.Monitor;

/// <summary>
/// 监控模块.
/// </summary>
[InjectModule<SDataMonitorSharedModule>]
[InjectModule<SDataMonitorApiModule>]
public class MonitorCoreModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddSingleton<ResourceSampler>();
        context.Services.AddHostedService(sp => sp.GetRequiredService<ResourceSampler>());
    }
}
