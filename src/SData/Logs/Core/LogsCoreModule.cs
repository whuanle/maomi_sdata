using Maomi;
using Microsoft.Extensions.DependencyInjection;
using SData.Logs.Api;

namespace SData.Logs;

/// <summary>
/// 日志模块.
/// </summary>
[InjectModule<SDataLogsSharedModule>]
[InjectModule<SDataLogsApiModule>]
public class LogsCoreModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
    }
}
