using Maomi;
using Microsoft.Extensions.DependencyInjection;
using SData.Dashboard.Api;

namespace SData.Dashboard;

/// <summary>
/// 仪表盘模块.
/// </summary>
[InjectModule<SDataDashboardSharedModule>]
[InjectModule<SDataDashboardApiModule>]
public class DashboardCoreModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddScoped<PanelQueryExecutor>();
    }
}
