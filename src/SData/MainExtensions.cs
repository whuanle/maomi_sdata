using Maomi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Serilog;
using Serilog.Configuration;
using Serilog.Settings.Configuration;
using SData.Infra;
using SData.Infra.Configuration;

namespace SData;

/// <summary>
/// 宿主构建扩展.
/// </summary>
public static class MainExtensions
{
    /// <summary>
    /// 构建宿主：SystemOptions（appsettings.json）→ Serilog（仅控制台）→ 模块系统.
    /// </summary>
    /// <param name="builder">构建器.</param>
    /// <returns>构建器.</returns>
    public static WebApplicationBuilder UseSData(this WebApplicationBuilder builder)
    {
        AppConst.ContentRoot = builder.Environment.ContentRootPath;
        AddConfigsOverride(builder);

        var systemOptions = builder.Configuration.GetSection("SData").Get<SystemOptions>() ?? new SystemOptions();

        // 监听端口
        builder.WebHost.UseUrls($"http://0.0.0.0:{systemOptions.Port}");

        builder.Services.AddSerilog((services, loggerConfiguration) =>
        {
            // 单文件发布下 Serilog 无法靠扫描磁盘目录发现 sink 程序集，需显式声明，
            // 否则 ReadFrom.Configuration 抛 "no Serilog:Using ... published as single-file"。日志只输出到控制台。
            var sinkOptions = new ConfigurationReaderOptions(typeof(ConsoleLoggerConfigurationExtensions).Assembly);
            loggerConfiguration
                .ReadFrom.Configuration(builder.Configuration, sinkOptions)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext();
        });

        // 内置 OpenAPI：仅开发模式注册与暴露（生产环境不生成文档，减少攻击面）
        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddOpenApi();
        }

        builder.Services.AddModule<MainModule>();
        return builder;
    }

    /// <summary>
    /// configs/appsettings.json 不存在时复制内容根目录下的 appsettings.json（Docker 挂载的 configs 通常为空）；已存在则不覆盖.
    /// </summary>
    /// <param name="contentRoot">内容根目录.</param>
    internal static void EnsureConfigsAppSettings(string contentRoot)
    {
        var source = Path.Combine(contentRoot, "appsettings.json");
        var directory = Path.Combine(contentRoot, AppConst.ConfigsDirectory);
        var target = Path.Combine(directory, "appsettings.json");
        if (File.Exists(target) || !File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        File.Copy(source, target);
    }

    /// <summary>
    /// 加载 configs/appsettings.json（可选）覆盖程序目录的 appsettings.json；优先级仍低于环境变量与命令行参数.
    /// </summary>
    /// <param name="builder">构建器.</param>
    private static void AddConfigsOverride(WebApplicationBuilder builder)
    {
        EnsureConfigsAppSettings(AppConst.ContentRoot);

        var directory = AppConst.GetPath(AppConst.ConfigsDirectory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        var configuration = builder.Configuration;
        configuration.AddJsonFile(new PhysicalFileProvider(directory), "appsettings.json", optional: true, reloadOnChange: false);

        // 新源追加在末尾，移到最后一个环境变量源之前：appsettings.json < configs/appsettings.json < 环境变量 < 命令行
        var sources = configuration.Sources;
        var added = sources[sources.Count - 1];
        sources.RemoveAt(sources.Count - 1);
        sources.Insert(sources.IndexOf(sources.Last(source => source is EnvironmentVariablesConfigurationSource)), added);
    }

    /// <summary>
    /// 中间件管线.
    /// </summary>
    /// <param name="app">应用.</param>
    /// <returns>应用.</returns>
    public static WebApplication UseSDataPipeline(this WebApplication app)
    {
        app.UseExceptionHandler();

        var environment = app.Environment;
        if (environment.IsDevelopment())
        {
            // 内置 OpenAPI（共享框架组件，零包依赖）：仅开发模式暴露 /openapi/v1.json
            app.MapOpenApi();
        }

        app.UseSerilogRequestLogging();

        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();

        app.UseMiddleware<SData.Rum.Api.RumHubOriginMiddleware>();

        app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/mcp"), branch =>
            branch.Use((context, next) => context.RequestServices.GetRequiredService<SData.Agents.AgentRequestBudget>().InvokeAsync(context, next)));
        app.MapMcp("/api/mcp");
        app.MapControllers();
        app.MapFallback("/api/{**path}", () => Results.Json(
            new { statusCode = 404, message = "接口不存在" },
            statusCode: StatusCodes.Status404NotFound));

        // OTLP gRPC 三服务（T112；AddGrpc 与服务注册在 ConfigureGrpcModule，服务类在 Ingest.Core/Grpc）
        app.MapGrpcService<SData.Ingest.OtlpGrpc.OtlpGrpcLogsService>();
        app.MapGrpcService<SData.Ingest.OtlpGrpc.OtlpGrpcTracesService>();
        app.MapGrpcService<SData.Ingest.OtlpGrpc.OtlpGrpcMetricsService>();

        // RUM 实时点击通道（T616；AddSignalR 在 ConfigureSignalRModule，hub 在 Rum.Api/Hubs）
        app.MapHub<SData.Rum.Hubs.RumLiveHub>("/hub/rum").RequireCors(ConfigureSignalRModule.RumCorsPolicy);

        // 前端静态资源（ui build 产物复制到 wwwroot）
        var systemOptions = app.Services.GetRequiredService<SystemOptions>();
        if (systemOptions.WebUI)
        {
            var indexPath = Path.Combine(AppConst.GetPath("wwwroot"), "index.html");
            if (File.Exists(indexPath))
            {
                app.UseDefaultFiles();
                app.UseStaticFiles();
                app.MapFallbackToFile("index.html");
            }
            else
            {
                app.Logger.LogWarning("WebUI enabled but wwwroot/index.html not found, run `npm run build` in ui/ first");
            }
        }

        return app;
    }
}
