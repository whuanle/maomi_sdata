using Maomi;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using SData.Infra.Configuration;
using SData.Settings.Handlers;
using Microsoft.Extensions.DependencyInjection;
using SData.Settings.Api;
using SData.Settings.Services;

namespace SData.Settings;

/// <summary>
/// 系统设置模块：运行时设置通道（SQLite SettingEntity 覆盖 + 文件回退）、设置读写 Handler.
/// </summary>
[InjectModule<SDataSettingsSharedModule>]
[InjectModule<SDataSettingsApiModule>]
public class SettingsCoreModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddSingleton<ISystemSettingsProvider, SystemSettingsProvider>();
        context.Services.AddSingleton<DuckDbSettingsService>();
        context.Services.AddDataProtection().SetApplicationName("SData");
        context.Services.AddOptions<KeyManagementOptions>().Configure<SystemOptions>((options, system) =>
        {
            var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(SData.Infra.AppConst.GetPath(), system.Database.Path.Replace('\\', '/'))))!, "notification-keys");
            options.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(
                new DirectoryInfo(directory), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        });
        context.Services.AddSingleton<ApiKeyProtection>();
        context.Services.AddSingleton<NotificationConfiguration>();
        context.Services.AddSingleton<NotificationTransport>();
        context.Services.AddScoped<NotificationService>();
        context.Services.AddScoped<INotificationSender>(provider => provider.GetRequiredService<NotificationService>());
    }
}
