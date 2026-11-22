using Microsoft.AspNetCore.SignalR;
using SData.Rum.Hubs;
using SData.Rum.Models;
using SData.Store;
using SData.Store.Models;

namespace SData.Rum.Api.Services;

/// <summary>
/// 实时点击发布器 SignalR 实现（T616）：入 IIngestBuffer（DuckDB 落库走既有批量链路）+ 广播 view:{projectKey} 组.
/// </summary>
public class HubRumLivePublisher : IRumLivePublisher
{
    private readonly IHubContext<RumLiveHub> _hubContext;
    private readonly IIngestBuffer _buffer;

    /// <summary>
    /// Initializes a new instance of the <see cref="HubRumLivePublisher"/> class.
    /// </summary>
    /// <param name="hubContext">RUM hub 上下文.</param>
    /// <param name="buffer">接入内存缓冲.</param>
    public HubRumLivePublisher(IHubContext<RumLiveHub> hubContext, IIngestBuffer buffer)
    {
        _hubContext = hubContext;
        _buffer = buffer;
    }

    /// <summary>
    /// 点击事件入缓冲并广播（缓冲满时仍广播，实时面板可见但不落库，与 beacon 429 语义一致）.
    /// </summary>
    /// <param name="record">落库记录.</param>
    /// <param name="click">实时推送载荷.</param>
    /// <returns>是否成功入缓冲.</returns>
    /// <inheritdoc/>
    public bool PublishClick(RumEventRecord record, RumLiveClickEvent click)
    {
        var accepted = _buffer.TryEnqueueRumEvent(record);
        _ = _hubContext.Clients
            .Group(RumLiveHub.ViewGroup(click.ProjectKey))
            .SendAsync("click", click);
        return accepted;
    }
}
