namespace SData.Infra.Models;

/// <summary>
/// 分页请求参数基类.
/// </summary>
public class PagedParameter
{
    /// <summary>
    /// 页码（从 1 开始）.
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// 页大小（1-200）.
    /// </summary>
    public int PageSize { get; set; } = 20;
}
