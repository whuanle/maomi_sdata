using System.Text.Json;

namespace SData.Rum;

/// <summary>RUM 允许站点的规范化与严格匹配.</summary>
public static class RumOrigins
{
    /// <summary>解析单个 HTTP/HTTPS Origin，不接受凭据、路径、查询、片段或通配符.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() ||
            value.Contains('*') || value.Contains(',') ||
            !System.Text.RegularExpressions.Regex.IsMatch(value, @"\Ahttps?://[^/?#\\\s]+/?\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    /// <summary>解析 JSON 字符串数组；旧裸域名和无效数据拒绝使用.</summary>
    public static bool TryParse(string? json, out string[] origins)
    {
        origins = [];
        try
        {
            using var document = JsonDocument.Parse(json ?? string.Empty);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var values = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || Normalize(item.GetString()) is not { } origin)
                {
                    return false;
                }

                values.Add(origin);
            }

            origins = values.Distinct(StringComparer.Ordinal).ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
