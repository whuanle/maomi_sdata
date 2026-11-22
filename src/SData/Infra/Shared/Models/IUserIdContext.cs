namespace SData.Infra.Models;

/// <summary>
/// 用户上下文接口，由控制器侧自动注入用户身份到请求模型.
/// </summary>
public interface IUserIdContext
{
    /// <summary>
    /// 当前用户 id（0 表示未登录）.
    /// </summary>
    long ContextUserId { get; set; }

    /// <summary>
    /// 当前用户角色.
    /// </summary>
    string ContextUserRole { get; set; }
}
