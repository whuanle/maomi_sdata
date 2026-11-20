using SData.Infra.Models;

namespace SData.Infra.Services;

/// <summary>
/// 将当前登录用户注入到实现 IUserIdContext 的请求模型（仅在控制器中调用）.
/// </summary>
public interface IUserContextProvider
{
    /// <summary>
    /// 注入用户上下文.
    /// </summary>
    /// <typeparam name="T">请求模型类型.</typeparam>
    /// <param name="request">请求模型.</param>
    void SetUserContext<T>(T request)
        where T : IUserIdContext;

    /// <summary>
    /// 获取当前用户 id（未登录返回 0）.
    /// </summary>
    /// <returns>用户 id.</returns>
    long GetCurrentUserId();
}
