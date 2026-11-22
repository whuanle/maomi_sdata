namespace SData.Database.Entities;

/// <summary>
/// 冷存档台账（T512）：一域一天一行，幂等归档依据（归档前查台账跳过已完成天）.
/// </summary>
public class ColdArchiveEntity : Audits.FullAuditedEntity
{
    /// <summary>
    /// id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 数据域（logs/traces/metrics/iot）.
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// 归档天（UTC yyyyMMdd 字符串，与天表名后缀一致）.
    /// </summary>
    public string Day { get; set; } = string.Empty;

    /// <summary>
    /// 冷层对象键（s3://... 或本地冷存目录路径）.
    /// </summary>
    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>
    /// 归档行数（与原天表 COUNT(*) 一致才落台账）.
    /// </summary>
    public long RowCount { get; set; }

    /// <summary>
    /// 归档完成时间（UTC）.
    /// </summary>
    public DateTimeOffset ArchivedTime { get; set; }
}
