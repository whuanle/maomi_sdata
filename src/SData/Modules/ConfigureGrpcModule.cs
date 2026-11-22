using Maomi;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SData.Ingest.OtlpGrpc;

namespace SData;

/// <summary>
/// gRPC 与内置标准端口（T112/C4）：
/// AddGrpc + OTLP gRPC 三服务注册；标准采集端口内置固化、不可配置——
/// 4317=OTLP gRPC（纯 HTTP/2，明文 h2c 下 Http1AndHttp2 实际只服务 HTTP/1，故须显式标记）、
/// 4318=OTLP HTTP/protobuf、9200=Elasticsearch _bulk 兼容（与 /v1/*、/_bulk 路由共享同一应用），
/// 主端口一并显式 Listen（Kestrel 存在显式 Listen 时 UseUrls 失效）.
/// </summary>
public class ConfigureGrpcModule : IModule
{
    /// <summary>OTLP gRPC 标准端口（纯 HTTP/2）.</summary>
    public const int OtlpGrpcPort = 4317;

    /// <summary>OTLP HTTP/protobuf 标准端口.</summary>
    public const int OtlpHttpPort = 4318;

    /// <summary>Elasticsearch _bulk 兼容标准端口.</summary>
    public const int EsBulkPort = 9200;

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
        context.Services.Configure<KestrelServerOptions>(options => ApplyListeners(options, mainPort));
    }

    /// <summary>
    /// 应用监听：主端口（HTTP/1+2）+ 内置标准端口 4317/4318/9200.
    /// 抽为静态函数便于单元测试；别名端口可注入以便测试避开固定端口冲突.
    /// </summary>
    /// <param name="options">Kestrel 配置.</param>
    /// <param name="mainPort">主端口（SData:Port，默认 5300）.</param>
    /// <param name="otlpGrpcPort">OTLP gRPC 端口（默认 4317）.</param>
    /// <param name="otlpHttpPort">OTLP HTTP 端口（默认 4318）.</param>
    /// <param name="esBulkPort">ES _bulk 端口（默认 9200）.</param>
    public static void ApplyListeners(KestrelServerOptions options, int mainPort, int otlpGrpcPort = OtlpGrpcPort, int otlpHttpPort = OtlpHttpPort, int esBulkPort = EsBulkPort)
    {
        options.ListenAnyIP(mainPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1AndHttp2);
        // 与主端口重叠时跳过（用户把 SData:Port 指到标准端口时的兜底，避免重复绑定启动报错）
        if (otlpGrpcPort != mainPort)
        {
            options.ListenAnyIP(otlpGrpcPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
        }

        if (otlpHttpPort != mainPort)
        {
            options.ListenAnyIP(otlpHttpPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1AndHttp2);
        }

        if (esBulkPort != mainPort)
        {
            options.ListenAnyIP(esBulkPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1AndHttp2);
        }
    }
}
