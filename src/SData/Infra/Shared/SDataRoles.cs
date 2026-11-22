namespace SData.Infra;

/// <summary>
/// 系统角色常量：单一超级管理员 + 可写/只读用户，不开放注册.
/// </summary>
public static class SDataRoles
{
    /// <summary>
    /// 超级管理员（系统种子唯一，管理用户与全部数据）.
    /// </summary>
    public const string Admin = "admin";

    /// <summary>
    /// 可写用户（可增删改业务数据）.
    /// </summary>
    public const string Write = "write";

    /// <summary>
    /// 只读用户（仅查询）.
    /// </summary>
    public const string ReadOnly = "readonly";

    /// <summary>
    /// 可写角色集合（授权用）：超级管理员或可写用户.
    /// </summary>
    public const string AdminOrWrite = Admin + "," + Write;
}
