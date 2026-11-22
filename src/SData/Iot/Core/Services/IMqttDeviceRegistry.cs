namespace SData.Iot.Services;

/// <summary>
/// MQTT 首次有效发布的设备台账入口.
/// </summary>
public interface IMqttDeviceRegistry
{
    /// <summary>设备管理状态版本，修改台账后使在线会话重新校验.</summary>
    long Version { get; }

    /// <summary>设备管理变更成功后失效会话缓存，不保留逐设备删除标记.</summary>
    void Invalidate();

    /// <summary>确保设备存在且可用；删除后重新注册，不恢复禁用设备.</summary>
    /// <param name="deviceKey">设备标识.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>设备是否可用.</returns>
    Task<bool> EnsureAsync(string deviceKey, CancellationToken cancellationToken);

    /// <summary>内存累积接收时间，后台批量回写.</summary>
    /// <param name="deviceKey">设备标识.</param>
    /// <param name="receivedAt">服务端接收时间.</param>
    void Touch(string deviceKey, DateTimeOffset receivedAt);
}
