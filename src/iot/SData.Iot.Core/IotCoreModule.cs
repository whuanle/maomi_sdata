using Maomi;
using Microsoft.Extensions.DependencyInjection;
using SData.Iot.Api;
using SData.Iot.Mqtt;
using SData.Iot.Services;

namespace SData.Iot;

/// <summary>
/// 物联网模块.
/// </summary>
[InjectModule<SDataIotSharedModule>]
[InjectModule<SDataIotApiModule>]
public class IotCoreModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddHostedService<MqttBrokerService>();

        // T141：设备凭证与 LastSeen 批量回写
        context.Services.AddSingleton<DeviceLastSeenBuffer>();
        context.Services.AddSingleton<DeviceCredentialService>();
        context.Services.AddSingleton<IMqttDeviceRegistry, MqttDeviceRegistry>();

        // T140↔T141 衔接：broker 经 IDeviceCredentialChecker 校验 CONNECT 凭证（与 DeviceCredentialService 同一单例）
        context.Services.AddSingleton<IDeviceCredentialChecker>(sp => sp.GetRequiredService<DeviceCredentialService>());
        context.Services.AddHostedService<DeviceLastSeenFlusher>();
    }
}
