namespace SData.Infra.Models;

/// <summary>
/// 分页响应.
/// </summary>
/// <typeparam name="T">行类型.</typeparam>
public class PagedResult<T>
{
    /// <summary>
    /// 当前页码.
    /// </summary>
    public int Page { get; set; }

    /// <summary>
    /// 页大小.
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// 总条数（-1 表示未知/未统计）.
    /// </summary>
    public long Count { get; set; } = -1;

    /// <summary>
    /// 数据行.
    /// </summary>
    public IReadOnlyList<T> Data { get; set; } = Array.Empty<T>();
}
