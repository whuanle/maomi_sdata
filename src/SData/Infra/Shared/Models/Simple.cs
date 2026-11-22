namespace SData.Infra.Models;

/// <summary>
/// 单值响应包装.
/// </summary>
/// <typeparam name="T">值类型.</typeparam>
public class Simple<T>
{
    /// <summary>
    /// 值.
    /// </summary>
    public T Value { get; set; } = default!;
}
