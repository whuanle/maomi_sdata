using Maomi;
using Microsoft.Extensions.DependencyInjection;
using SData.Analytics.Api;

namespace SData.Analytics;

/// <summary>
/// 分析模块.
/// </summary>
[InjectModule<SDataAnalyticsSharedModule>]
[InjectModule<SDataAnalyticsApiModule>]
public class AnalyticsCoreModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
    }
}
