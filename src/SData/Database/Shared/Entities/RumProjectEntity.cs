namespace SData.Database.Entities;

/// <summary>
/// RUM 监测项目（T611）.
/// </summary>
public class RumProjectEntity : Audits.FullAuditedEntity
{
    /// <summary>
    /// id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 项目标识（唯一，C3 上报凭证）.
    /// </summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>
    /// 项目名称.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 允许上报域名（JSON 数组字符串，空数组 = 不限）.
    /// </summary>
    public string DomainsJson { get; set; } = "[]";

    /// <summary>
    /// 性能事件采样率（0–1）.
    /// </summary>
    public double PerfSampleRate { get; set; } = 1.0;

    /// <summary>
    /// 会话回放采样率（0–1）.
    /// </summary>
    public double ReplaySampleRate { get; set; } = 1.0;
}
