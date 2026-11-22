using System.Globalization;
using System.Text.RegularExpressions;

namespace SData.Settings;

/// <summary>DuckDB 可调整设置（不含路径、线程等启动参数）.</summary>
public class DuckDbSettings
{
    /// <summary>内存限制.</summary>
    public string MemoryLimit { get; set; } = string.Empty;
}

/// <summary>DuckDB 设置及实例当前生效值.</summary>
public class DuckDbSettingsResponse : DuckDbSettings
{
    /// <summary>DuckDB 实际生效限制（可能使用二进制单位展示）.</summary>
    public string EffectiveMemoryLimit { get; set; } = string.Empty;
}

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

/// <summary>实例级运行参数通道（内存、线程、并发）；Store.Core 实现，设置模块不依赖存储实现.</summary>
public interface IDuckDbRuntimeSettings
{
    /// <summary>当前生效限制；不执行查询.</summary>
    string MemoryLimit { get; }

    /// <summary>DuckDB 读回的实际限制.</summary>
    string EffectiveMemoryLimit { get; }

    /// <summary>在写入门内绑定参数设置并读回当前限制.</summary>
    Task ApplyMemoryLimitAsync(string memoryLimit, CancellationToken cancellationToken);

    /// <summary>在写入门内应用线程数（SET threads，立即生效）.</summary>
    Task ApplyThreadsAsync(int threads, CancellationToken cancellationToken);

    /// <summary>替换查询准入门应用同时执行的查询数（立即生效）.</summary>
    void ApplyMaxConcurrentQueries(int maxConcurrentQueries);
}
