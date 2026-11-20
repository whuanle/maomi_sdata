namespace SData.Database.Entities;

/// <summary>
/// 系统键值配置.
/// </summary>
public class SettingEntity
{
    /// <summary>
    /// id.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// 键（唯一）.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// 值.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}
