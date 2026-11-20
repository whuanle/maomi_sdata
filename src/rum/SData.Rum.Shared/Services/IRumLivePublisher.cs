using SData.Rum.Models;
using SData.Store.Models;

namespace SData.Rum;

/// <summary>
/// 实时点击发布器（beacon/hub 双通道共用入口：入摄取缓冲 + 广播 view:{projectKey} 订阅组；实现含 IHubContext 依赖，放 Rum.Api）.
/// </summary>
public interface IRumLivePublisher
{
    /// <summary>
    /// 点击事件入摄取缓冲并实时广播.
    /// </summary>
    /// <param name="record">落库记录（event_type=click）.</param>
    /// <param name="click">实时推送载荷.</param>
    /// <returns>是否成功入缓冲（false = 缓冲满丢弃；广播不受影响）.</returns>
    bool PublishClick(RumEventRecord record, RumLiveClickEvent click);
}
