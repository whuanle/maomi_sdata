using Maomi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    /// 构建宿主：导入配置 → SystemOptions → Serilog → 模块系统.
    /// </summary>
    /// <param name="builder">构建器.</param>
    /// <returns>构建器.</returns>
    public static WebApplicationBuilder UseSData(this WebApplicationBuilder builder)
    {
        AppConst.ContentRoot = builder.Environment.ContentRootPath;

        ImportSystemConfiguration(builder);

        var systemOptions = builder.Configuration.GetSection("SData").Get<SystemOptions>()
            ?? throw new FormatException("The system configuration (SData section) cannot be loaded.");

        // 监听端口
        builder.WebHost.UseUrls($"http://0.0.0.0:{systemOptions.Port}");

        builder.Services.AddSerilog((services, loggerConfiguration) =>
        {
            // 单文件发布下 Serilog 无法靠扫描磁盘目录发现 sink 程序集，需显式声明，
            // 否则 ReadFrom.Configuration 抛 "no Serilog:Using ... published as single-file"。
            var sinkOptions = new ConfigurationReaderOptions(
                typeof(ConsoleLoggerConfigurationExtensions).Assembly,
                typeof(FileLoggerConfigurationExtensions).Assembly);
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

    private static void ImportSystemConfiguration(WebApplicationBuilder builder)
    {
        // 加载顺序：SDATA_FILE 环境变量 → 当前目录/内容根/程序目录及其上级目录的 configs
        var candidates = new List<string>();

        var file = Environment.GetEnvironmentVariable("SDATA_FILE");
        if (!string.IsNullOrEmpty(file))
        {
            candidates.Add(file);
        }

        var roots = new List<string>
        {
            Directory.GetCurrentDirectory(),
            AppConst.ContentRoot,
            AppContext.BaseDirectory,
        };

        // dotnet run 时内容根是项目目录，向上查找仓库根的 configs
        foreach (var root in roots.ToArray())
        {
            var directory = new DirectoryInfo(root);
            for (var i = 0; i < 3 && directory != null; i++)
            {
                roots.Add(directory.FullName);
                directory = directory.Parent;
            }
        }

        foreach (var root in roots)
        {
            candidates.Add(Path.Combine(root, "configs", "system.yaml"));
            candidates.Add(Path.Combine(root, "configs", "system.json"));
        }

        var configFile = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("System config not found (configs/system.yaml). Set SDATA_FILE to override.");

        var extension = Path.GetExtension(configFile).ToLowerInvariant();
        if (extension is ".yaml" or ".yml")
        {
            builder.Configuration.AddYamlFile(configFile, optional: false, reloadOnChange: false);
        }
        else
        {
            builder.Configuration.AddJsonFile(configFile, optional: false, reloadOnChange: false);
        }
    }
}
