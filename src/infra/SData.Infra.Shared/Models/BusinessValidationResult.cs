namespace SData.Infra.Models;

/// <summary>
/// 统一业务错误响应体.
/// </summary>
public class BusinessValidationResult
{
    /// <summary>
    /// HTTP 状态码.
    /// </summary>
    public int StatusCode { get; set; } = 500;

    /// <summary>
    /// i18n 消息 key.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 详情.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// 字段级验证错误.
    /// </summary>
    public IDictionary<string, IEnumerable<string>>? Errors { get; set; }
}
