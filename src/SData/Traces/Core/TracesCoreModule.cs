using Maomi;
using Microsoft.Extensions.DependencyInjection;
using SData.Traces.Api;

namespace SData.Traces;

/// <summary>
/// 链路模块.
/// </summary>
[InjectModule<SDataTracesSharedModule>]
[InjectModule<SDataTracesApiModule>]
public class TracesCoreModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
    }
}
