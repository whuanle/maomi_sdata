namespace SData.Infra.Configuration;

/// <summary>
/// 外部 OTLP 请求头规则；两个协议使用相同的文本 metadata 子集.
/// </summary>
public static class OtlpHeaders
{
    /// <summary>设置接口秘密占位符.</summary>
    public const string SecretMask = "******";

    /// <summary>验证 SDK 逗号分隔的 key=value 格式，不在错误中包含秘密.</summary>
    /// <param name="headers">请求头.</param>
    /// <returns>是否有效.</returns>
    public static bool IsValid(string? headers)
    {
        if (headers?.Length > 8192 || headers?.Any(c => c < 32 || c > 126) == true)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(headers))
        {
            return true;
        }

        if (headers.Length > 8192)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in headers.Split(','))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                return false;
            }

            var name = entry[..separator].Trim();
            var value = Uri.UnescapeDataString(entry[(separator + 1)..].Trim());
            if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                || name.StartsWith("grpc-", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-bin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("content-type", StringComparison.OrdinalIgnoreCase)
                || name.Equals("content-length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("te", StringComparison.OrdinalIgnoreCase)
                || !names.Add(name)
                || value.Length == 0
                || value.Any(c => c < 32 || c > 126))
            {
                return false;
            }

            // 禁止换行即使写在名称或等号两侧，避免 Trim 掩盖非法输入。
            if (entry.Any(c => c is '\r' or '\n'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>统一 metadata 名称大小写，保留值与 SDK 编码语义.</summary>
    /// <param name="headers">已验证请求头.</param>
    /// <returns>SDK 请求头字符串.</returns>
    public static string Normalize(string? headers)
    {
        return string.IsNullOrWhiteSpace(headers) ? string.Empty : string.Join(",", headers.Split(',').Select(entry =>
        {
            var separator = entry.IndexOf('=');
            return entry[..separator].Trim().ToLowerInvariant() + "=" + entry[(separator + 1)..].Trim();
        }));
    }

    /// <summary>返回独立脱敏快照，不能修改 provider 的真实配置.</summary>
    /// <param name="options">真实配置.</param>
    /// <returns>接口安全配置.</returns>
    public static OtlpOptions Redact(OtlpOptions options) => new()
    {
        Trace = options.Trace,
        Metrics = options.Metrics,
        Protocol = options.Protocol,
        TraceHeaders = string.IsNullOrEmpty(options.TraceHeaders) ? string.Empty : SecretMask,
        MetricsHeaders = string.IsNullOrEmpty(options.MetricsHeaders) ? string.Empty : SecretMask,
    };
}
