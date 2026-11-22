namespace SData.Store.Models;

/// <summary>
/// 分析查询结果.
/// </summary>
public class AnalyticsQueryResult
{
    /// <summary>
    /// 列名.
    /// </summary>
    public List<string> Columns { get; set; } = new();

    /// <summary>
    /// 数据行（每行为列值数组）.
    /// </summary>
    public List<List<object?>> Rows { get; set; } = new();

    /// <summary>
    /// 耗时（毫秒）.
    /// </summary>
    public long ElapsedMs { get; set; }

    /// <summary>
    /// 是否被 rowLimit 截断.
    /// </summary>
    public bool Truncated { get; set; }
}
