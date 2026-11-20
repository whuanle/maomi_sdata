using Microsoft.AspNetCore.SignalR;
using SData.Rum.Models;
using SData.Rum.Api;

namespace SData.Rum.Hubs;

/// <summary>
/// RUM 实时通道 hub（/hub/rum，T616）：
/// 探针匿名连接（握手 query ?projectKey=）经 SendClick 直报点击（低延迟，断线回落 beacon 批量通道）；
/// 管理端（JWT access_token query 认证）Subscribe 加入 view:{projectKey} 组接收 click 推送.
/// </summary>
public class RumLiveHub : Hub
{
    /// <summary>订阅组前缀（view:{projectKey}）.</summary>
    public const string ViewGroupPrefix = "view:";

    /// <summary>连接项键：握手携带的项目标识.</summary>
    private const string ProjectKeyItem = "projectKey";

    private readonly IRumLivePublisher _publisher;
    private readonly IRumOriginPolicy _originPolicy;

    /// <summary>
    /// Initializes a new instance of the <see cref="RumLiveHub"/> class.
    /// </summary>
    /// <param name="publisher">实时点击发布器.</param>
    public RumLiveHub(IRumLivePublisher publisher, IRumOriginPolicy originPolicy)
    {
        _publisher = publisher;
        _originPolicy = originPolicy;
    }

    /// <inheritdoc/>
    public override Task OnConnectedAsync()
    {
        var projectKey = Context.GetHttpContext()?.Request.Query["projectKey"].FirstOrDefault();
        if (!string.IsNullOrEmpty(projectKey))
        {
            Context.Items[ProjectKeyItem] = projectKey;
        }

        return base.OnConnectedAsync();
    }

    /// <summary>
    /// 探针直报一次点击（与 beacon 同策略：projectKey 缺失拒绝；入缓冲 + 广播）.
    /// </summary>
    /// <param name="click">点击载荷.</param>
    /// <returns>是否成功入缓冲（false = 缓冲满，探针应回落批量通道）.</returns>
    public Task<bool> SendClick(RumLiveClickEvent click)
    {
        var projectKey = Context.Items.TryGetValue(ProjectKeyItem, out var value) ? value as string : null;
        if (string.IsNullOrEmpty(projectKey) || click is null)
        {
            throw new HubException("projectKey 缺失，请以 /hub/rum?projectKey= 建立连接");
        }

        var request = Context.GetHttpContext()?.Request;
        if (request == null || !_originPolicy.Allows(projectKey, RumRequestOrigin.Read(request)))
        {
            throw new HubException("项目或上报来源不允许");
        }

        click.ProjectKey = projectKey;
        if (string.IsNullOrEmpty(click.SessionId))
        {
            click.SessionId = Context.ConnectionId;
        }

        if (click.Timestamp == default)
        {
            click.Timestamp = DateTimeOffset.UtcNow;
        }

        return Task.FromResult(_publisher.PublishClick(click.ToRecord(), click));
    }

    /// <summary>
    /// 管理端订阅项目实时点击流（需已认证，JWT 经 query access_token）.
    /// </summary>
    /// <param name="projectKey">项目标识.</param>
    /// <returns>任务.</returns>
    public async Task Subscribe(string projectKey)
    {
        if (Context.User?.Identity?.IsAuthenticated != true)
        {
            throw new HubException("实时订阅需要登录认证（access_token）");
        }

        if (string.IsNullOrEmpty(projectKey))
        {
            throw new HubException("projectKey 不能为空");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ViewGroup(projectKey));
    }

    /// <summary>
    /// 退出项目实时点击流.
    /// </summary>
    /// <param name="projectKey">项目标识.</param>
    /// <returns>任务.</returns>
    public Task Unsubscribe(string projectKey)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, ViewGroup(projectKey));
    }

    /// <summary>
    /// 项目订阅组名.
    /// </summary>
    /// <param name="projectKey">项目标识.</param>
    /// <returns>组名.</returns>
    public static string ViewGroup(string projectKey)
    {
        return ViewGroupPrefix + projectKey;
    }
}
