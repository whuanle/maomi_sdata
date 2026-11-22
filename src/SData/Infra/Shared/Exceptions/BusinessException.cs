using SData.Infra.Models;

namespace SData.Infra.Exceptions;

/// <summary>
/// 业务异常，message 为 i18n key，由 I18n 中间件按 Accept-Language 翻译.
/// </summary>
public class BusinessException : Exception
{
    /// <summary>
    /// 业务错误码（同时作为 HTTP 状态码返回给前端）.
    /// </summary>
    public int StatusCode { get; set; } = 500;

    /// <summary>
    /// 错误详情.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="BusinessException"/> class.
    /// </summary>
    public BusinessException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BusinessException"/> class.
    /// </summary>
    public BusinessException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BusinessException"/> class.
    /// </summary>
    public BusinessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// 快捷构造：指定错误码与 i18n message.
    /// </summary>
    public static BusinessException Throw(int statusCode, string message, string? detail = null)
    {
        return new BusinessException(message)
        {
            StatusCode = statusCode,
            Detail = detail,
        };
    }
}
