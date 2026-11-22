namespace SData.Database.Entities;

/// <summary>
/// 仪表盘/大屏配置.
/// </summary>
public class DashboardEntity : Audits.FullAuditedEntity
{
    /// <summary>
    /// id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 名称.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 类型：view（常规仪表盘）/ screen（大屏）.
    /// </summary>
    public string Kind { get; set; } = "view";

    /// <summary>
    /// 描述.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 面板配置（JSON）.
    /// </summary>
    public string ConfigJson { get; set; } = "{}";
}
