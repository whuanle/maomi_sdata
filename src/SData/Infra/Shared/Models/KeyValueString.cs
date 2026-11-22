namespace SData.Infra.Models;

/// <summary>
/// 键值对响应.
/// </summary>
public class KeyValueString
{
    /// <summary>
    /// 键.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// 值.
    /// </summary>
    public string? Value { get; set; }
}
