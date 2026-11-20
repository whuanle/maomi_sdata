using Maomi;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SData;

/// <summary>
/// SignalR 实时通道（T616）：AddSignalR + RUM 探针跨域 CORS 策略
/// （项目允许站点 + 可选 SData:Rum:CorsOrigins 上限；hub 映射在 MainExtensions.UseSDataPipeline）.
/// </summary>
public class ConfigureSignalRModule : IModule
{
    /// <summary>RUM 探针跨域策略名.</summary>
    public const string RumCorsPolicy = "RumLiveCors";

    private readonly IConfiguration _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigureSignalRModule"/> class.
    /// </summary>
    /// <param name="configuration">配置.</param>
    public ConfigureSignalRModule(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddSignalR(options =>
        {
            // 探针排障需要；生产保持关闭避免细节泄露
            options.EnableDetailedErrors = _configuration.GetValue("SData:DetailedErrors", false);
        });

        context.Services.AddCors();
        context.Services.AddSingleton<Microsoft.AspNetCore.Cors.Infrastructure.ICorsPolicyProvider, RumCorsPolicyProvider>();
    }
}
