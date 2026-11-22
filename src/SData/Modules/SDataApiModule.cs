using Maomi;
using Microsoft.Extensions.DependencyInjection;

namespace SData;

/// <summary>
/// 宿主 API 汇总模块：MVC、认证、Mediator、OpenTelemetry.
/// </summary>
[InjectModule<ConfigureAuthorizaModule>]
[InjectModule<ConfigureMVCModule>]
[InjectModule<ConfigureMediatorModule>]
[InjectModule<ConfigureOpenTelemetryModule>]
public class SDataApiModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
    }
}
