using Maomi;
using SData.Metrics.Promql;
using Microsoft.Extensions.DependencyInjection;
using SData.Metrics.Api;

namespace SData.Metrics;

/// <summary>
/// 指标模块.
/// </summary>
[InjectModule<SDataMetricsSharedModule>]
[InjectModule<SDataMetricsApiModule>]
public class MetricsCoreModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        // DuckDbConnectionManager / ITableResolver 由 StoreCoreModule 注册，此处仅注册 PromQL 执行器
        context.Services.AddSingleton<SData.Metrics.IPromqlExecutor, PromqlExecutor>();
    }
}
