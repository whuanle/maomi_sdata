using Maomi;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Settings.Services;
using SData.Store;

namespace SData;

/// <summary>
/// 自身遥测固定写入本机，并可额外导出到外部 OTLP.
/// </summary>
public class ConfigureOpenTelemetryModule : IModule
{
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigureOpenTelemetryModule"/> class.
    /// </summary>
    /// <param name="configuration">配置.</param>
    public ConfigureOpenTelemetryModule(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        var systemOptions = _configuration.GetSection("SData").Get<SystemOptions>() ?? new SystemOptions();

        // 外部出口设置重启生效；读取失败回退文件配置，不影响固定本地出口。
        var sqlitePath = Path.Combine(AppConst.GetPath(), systemOptions.Database.Path.Replace('\\', '/'));
        var otlp = SystemSettingsProvider.TryLoadOtlpOverride(sqlitePath) ?? systemOptions.OTLP;
        var builder = context.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(AppConst.ServiceName, serviceInstanceId: Environment.MachineName));

        builder.WithTracing(tracing =>
        {
            tracing
                .AddSource(AppConst.ActivitySourceName)
                .AddAspNetCoreInstrumentation(options => options.Filter = http => !IsOtlpRequest(http.Request.Path))
                .AddHttpClientInstrumentation()
                .AddProcessor(provider => new BatchActivityExportProcessor(
                    new LocalTraceExporter(provider.GetRequiredService<IIngestBuffer>())));

            if (TryGetEndpoint(otlp.Trace, otlp.Protocol, out var endpoint) && OtlpHeaders.IsValid(otlp.TraceHeaders))
            {
                tracing.AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = endpoint!;
                    exporter.Protocol = ParseProtocol(otlp.Protocol);
                    exporter.Headers = OtlpHeaders.Normalize(otlp.TraceHeaders);
                });
            }
        });

        builder.WithMetrics(metrics =>
        {
            metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(AppConst.ActivitySourceName)
                .AddReader(provider => new PeriodicExportingMetricReader(
                    new LocalMetricExporter(provider.GetRequiredService<IIngestBuffer>()))
                {
                    TemporalityPreference = MetricReaderTemporalityPreference.Cumulative,
                });

            if (TryGetEndpoint(otlp.Metrics, otlp.Protocol, out var endpoint) && OtlpHeaders.IsValid(otlp.MetricsHeaders))
            {
                metrics.AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = endpoint!;
                    exporter.Protocol = ParseProtocol(otlp.Protocol);
                    exporter.Headers = OtlpHeaders.Normalize(otlp.MetricsHeaders);
                });
            }
        });
    }

    /// <summary>
    /// 排除接收数据平面的链路，避免外部出口指回本机时形成递归.
    /// </summary>
    /// <param name="path">请求路径.</param>
    /// <returns>是否为 OTLP 接收请求.</returns>
    public static bool IsOtlpRequest(PathString path)
    {
        return path.StartsWithSegments("/v1")
            || path.StartsWithSegments("/api/v1")
            || path.StartsWithSegments("/api/ingest/otlp/v1")
            || path.StartsWithSegments("/opentelemetry.proto.collector.trace.v1.TraceService")
            || path.StartsWithSegments("/opentelemetry.proto.collector.metrics.v1.MetricsService")
            || path.StartsWithSegments("/opentelemetry.proto.collector.logs.v1.LogsService");
    }

    private static bool TryGetEndpoint(string? value, string? protocol, out Uri? endpoint)
    {
        endpoint = null;
        return protocol is "grpc" or "http/protobuf"
            && Uri.TryCreate(value?.Trim(), UriKind.Absolute, out endpoint)
            && endpoint.Scheme is "http" or "https";
    }

    private static OpenTelemetry.Exporter.OtlpExportProtocol ParseProtocol(string protocol)
    {
        return protocol == "http/protobuf"
            ? OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf
            : OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
    }
}
