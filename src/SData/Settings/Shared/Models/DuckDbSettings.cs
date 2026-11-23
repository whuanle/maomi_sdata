using System.Globalization;
using System.Text.RegularExpressions;

namespace SData.Settings;

/// <summary>设置容量白名单；十进制 MB/GB 与二进制 MiB/GiB 分别计算.</summary>
public static class DuckDbMemoryLimit
{
    /// <summary>与前端安全整数范围一致，防止容量运算溢出.</summary>
    public const long MaxBytes = 9_007_199_254_740_991;

    /// <summary>校验并规范化用户输入；禁止无限制及 SQL 片段.</summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 40)
        {
            return false;
        }

        var match = Regex.Match(value.Trim(), @"^([0-9]{1,16}(?:\.[0-9]{1,6})?)\s*(MB|GB|MiB|GiB)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size))
        {
            return false;
        }

        var (unit, multiplier) = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "MB" => ("MB", 1_000_000L),
            "GB" => ("GB", 1_000_000_000L),
            "MIB" => ("MiB", 1_048_576L),
            _ => ("GiB", 1_073_741_824L),
        };
        var bytes = size * multiplier;
        if (bytes < 1 || bytes > MaxBytes)
        {
            return false;
        }

        normalized = size.ToString("0.######", CultureInfo.InvariantCulture) + unit;
        return true;
    }
}

