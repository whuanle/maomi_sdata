using Microsoft.AspNetCore.Http;

namespace SData.Rum.Api;

/// <summary>在协议升级前验证探针来源；管理端仅允许已认证同源连接.</summary>
public class RumHubOriginMiddleware(RequestDelegate next)
{
    /// <summary>验证每次握手，不能依赖浏览器 CORS 限制 WebSocket.</summary>
    public async Task InvokeAsync(HttpContext context, IRumOriginPolicy policy)
    {
        if (context.Request.Path.StartsWithSegments("/hub/rum"))
        {
            var origin = RumRequestOrigin.Read(context.Request);
            var projectKey = context.Request.Query["projectKey"];
            var allowed = projectKey.Count == 1 && !string.IsNullOrEmpty(projectKey[0])
                ? policy.Allows(projectKey[0]!, origin)
                : context.User.Identity?.IsAuthenticated == true && RumRequestOrigin.IsSameOrigin(context.Request, origin);
            if (!allowed)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        await next(context);
    }
}
