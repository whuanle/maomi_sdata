using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SData.Infra.Exceptions;
using SData.Infra.Models;

namespace SData.Infra.Api;

/// <summary>
/// 全局业务异常处理：BusinessException → 统一错误响应（message 为 i18n key）.
/// </summary>
public class BusinessExceptionHandler : IExceptionHandler
{
    private readonly ILogger<BusinessExceptionHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BusinessExceptionHandler"/> class.
    /// </summary>
    /// <param name="logger">日志.</param>
    public BusinessExceptionHandler(ILogger<BusinessExceptionHandler> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BusinessException businessException)
        {
            return false;
        }

        var error = new BusinessValidationResult
        {
            StatusCode = businessException.StatusCode,
            Message = businessException.Message,
            Detail = businessException.Detail,
        };

        httpContext.Response.StatusCode = businessException.StatusCode == 0 ? 500 : businessException.StatusCode;
        httpContext.Response.ContentType = "application/json";
        await httpContext.Response.WriteAsJsonAsync(error, cancellationToken);
        return true;
    }
}
