namespace SData.Database.Entities;

/// <summary>
/// 保存的分析查询.
/// </summary>
public class SavedQueryEntity : Audits.FullAuditedEntity
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
    /// 类型（sql = 分析 SQL；search = 统一搜索页保存的搜索）.
    /// </summary>
    public string Kind { get; set; } = "sql";

    /// <summary>
    /// DuckDB SQL（仅允许 SELECT/WITH；kind=search 时为空）.
    /// </summary>
    public string SqlText { get; set; } = string.Empty;

    /// <summary>
    /// 搜索状态 JSON（kind=search 时有效：domain/query/rangeMs）.
    /// </summary>
    public string Definition { get; set; } = string.Empty;

    /// <summary>
    /// 描述.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
