using Maomi;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SData.Ingest.OtlpGrpc;

namespace SData;

/// <summary>
/// gRPC 与通用附加监听配置（T112/C4）：
/// AddGrpc + OTLP gRPC 三服务注册；SData:Listeners:[{Port,Protocol:http|grpc}] 通用附加端口
/// （4317/4318/9200 全部由此实现，grpc=纯 HTTP/2，http=HTTP/1+2）；显式 Listen 主端口与附加端口
/// （Kestrel 存在显式 Listen 时 UseUrls 失效，故主端口在此一并 Listen）.
/// </summary>
public class ConfigureGrpcModule : IModule
{
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigureGrpcModule"/> class.
    /// </summary>
    /// <param name="configuration">配置.</param>
    public ConfigureGrpcModule(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddScoped<IngestAuthenticationInterceptor>();
        context.Services.AddGrpc(options => options.Interceptors.Add<IngestAuthenticationInterceptor>());

        // gRPC 服务按请求激活，注册为 Scoped 才能消费 Scoped 的 IIngestIngestor（Singleton 会触发作用域校验失败）
        context.Services.AddScoped<OtlpGrpcLogsService>();
        context.Services.AddScoped<OtlpGrpcTracesService>();
        context.Services.AddScoped<OtlpGrpcMetricsService>();

        var mainPort = _configuration.GetValue("SData:Port", 5300);
        var listeners = _configuration.GetSection("SData:Listeners").Get<List<IngestListenerOptions>>() ?? new List<IngestListenerOptions>();
        context.Services.Configure<KestrelServerOptions>(options => ApplyListeners(options, mainPort, listeners));
    }

    /// <summary>
    /// 应用监听配置：主端口（HTTP/1+2）+ 附加端口（按协议标记）.
    /// 抽为静态函数便于单元测试.
    /// </summary>
    /// <param name="options">Kestrel 配置.</param>
    /// <param name="mainPort">主端口（SData:Port）.</param>
    /// <param name="listeners">附加监听列表（SData:Listeners）.</param>
    public static void ApplyListeners(KestrelServerOptions options, int mainPort, List<IngestListenerOptions> listeners)
    {
        options.ListenAnyIP(mainPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1AndHttp2);
        foreach (var listener in listeners)
        {
            var protocols = string.Equals(listener.Protocol, "grpc", StringComparison.OrdinalIgnoreCase)
                ? HttpProtocols.Http2
                : HttpProtocols.Http1AndHttp2;
            options.ListenAnyIP(listener.Port, listenOptions => listenOptions.Protocols = protocols);
        }
    }
}

/// <summary>
/// 附加监听项（C4：SData:Listeners）.
/// </summary>
public class IngestListenerOptions
{
    /// <summary>监听端口.</summary>
    public int Port { get; set; }

    /// <summary>协议标记：http | grpc.</summary>
    public string Protocol { get; set; } = "http";
}
