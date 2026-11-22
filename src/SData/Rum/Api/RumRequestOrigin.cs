using Microsoft.AspNetCore.Http;

namespace SData.Rum.Api;

/// <summary>只使用浏览器请求头解析来源，不信任事件数据或转发头.</summary>
public static class RumRequestOrigin
{
    /// <summary>Origin 优先；仅缺失时允许 Referer 回退.</summary>
    public static string? Read(HttpRequest request)
    {
        if (request.Headers.ContainsKey("Origin"))
        {
            var values = request.Headers["Origin"];
            return values.Count == 1 ? RumOrigins.Normalize(values[0]) : null;
        }

        var referers = request.Headers["Referer"];
        if (referers.Count != 1 || !Uri.TryCreate(referers[0], UriKind.Absolute, out var referer) ||
            !string.IsNullOrEmpty(referer.UserInfo))
        {
            return null;
        }

        return RumOrigins.Normalize(referer.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>当前服务的同源地址，仅用于已认证管理端连接.</summary>
    public static bool IsSameOrigin(HttpRequest request, string? origin) =>
        origin != null && origin == RumOrigins.Normalize($"{request.Scheme}://{request.Host}");
}
