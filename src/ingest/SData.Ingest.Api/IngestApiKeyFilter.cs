using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SData.Infra.Configuration;

namespace SData.Ingest.Api;

/// <summary>
/// 接入端点鉴权过滤器：X-Api-Key 头校验（可在配置中关闭）.
/// </summary>
public class IngestApiKeyFilter : IAsyncAuthorizationFilter
{
    private readonly SData.Ingest.IIngestKeyValidator _validator;
    private readonly SystemOptions _systemOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestApiKeyFilter"/> class.
    /// </summary>
    /// <param name="validator">key 校验器.</param>
    /// <param name="systemOptions">系统配置.</param>
    public IngestApiKeyFilter(SData.Ingest.IIngestKeyValidator validator, SystemOptions systemOptions)
    {
        _validator = validator;
        _systemOptions = systemOptions;
    }

    /// <inheritdoc/>
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var apiKey = ExtractApiKey(context.HttpContext.Request.Headers);
        if (!await _validator.ValidateAsync(apiKey ?? string.Empty))
        {
            context.Result = new ObjectResult(new SData.Infra.Models.BusinessValidationResult
            {
                StatusCode = 401,
                Message = "接入鉴权失败，请提供有效的 X-Api-Key",
            })
            {
                StatusCode = 401,
            };
        }
    }

    /// <summary>
    /// 提取接入 key：X-Api-Key 头优先；其次 Authorization Bearer；再次 Basic（username 任意，password=Key，Filebeat/Collector 惯例）.
    /// </summary>
    /// <param name="headers">请求头.</param>
    /// <returns>接入 key（未提供为 null）.</returns>
    public static string? ExtractApiKey(IHeaderDictionary headers)
    {
        if (headers["X-Api-Key"].Count > 1 || headers.Authorization.Count > 1) return null;
        var apiKey = headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(apiKey))
        {
            return apiKey;
        }

        var authorization = headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authorization))
        {
            return null;
        }

        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var encoded = authorization["Basic ".Length..].Trim();
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                // Basic 形态 username:password，password=Key（username 任意）
                var separator = decoded.IndexOf(':');
                return separator >= 0 ? decoded[(separator + 1)..] : decoded;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }
}
