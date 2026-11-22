using Maomi;
using Microsoft.Extensions.DependencyInjection;
using SData.Ingest.Api;
using SData.Ingest.Consumers;
using SData.Ingest.Services;

namespace SData.Ingest;

/// <summary>
/// 接入模块.
/// </summary>
[InjectModule<SDataIngestSharedModule>]
[InjectModule<SDataIngestApiModule>]
public class IngestCoreModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddSingleton<IngestKeyValidator>();
        context.Services.AddSingleton<SData.Ingest.IIngestKeyValidator>(sp => sp.GetRequiredService<IngestKeyValidator>());
        context.Services.AddSingleton<SData.Ingest.IOtlpParser, OtlpParser>();
        context.Services.AddSingleton<SData.Ingest.IOtlpProtoParser, OtlpProtoParserService>();
        context.Services.AddSingleton<SData.Ingest.IEsBulkParser, EsBulkParserService>();
        context.Services.AddScoped<IngestService>();
        context.Services.AddScoped<SData.Ingest.IIngestIngestor>(sp => sp.GetRequiredService<IngestService>());
        // MQ 日志消费（多实例热更新：管理器周期检测实例配置差异启停消费者；解析预览器供设置页调试规则）
        context.Services.AddSingleton<IMqLogDecoder, MqLogDecoder>();
        context.Services.AddSingleton<SData.Settings.IMqLogPreviewer, MqLogPreviewer>();
        context.Services.AddSingleton<MqConsumerManagerService>();
        context.Services.AddSingleton<IMqConsumerControl>(sp => sp.GetRequiredService<MqConsumerManagerService>());
        context.Services.AddHostedService(sp => sp.GetRequiredService<MqConsumerManagerService>());
        context.Services.AddHostedService<MqConsumptionSampler>();
        context.Services.AddHostedService<MqNotificationWorker>();
    }
}
