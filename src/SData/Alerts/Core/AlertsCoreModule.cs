using Maomi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SData.Alerts.Api;

namespace SData.Alerts;

[InjectModule<AlertsSharedModule>]
[InjectModule<AlertsApiModule>]
public class AlertsCoreModule : IModule
{
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.TryAddSingleton(TimeProvider.System);
        context.Services.AddSingleton<AlertExecutionGate>();
        context.Services.AddHostedService<AlertScheduler>();
        context.Services.AddHostedService<AlertDeliveryWorker>();
    }
}
