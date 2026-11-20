using System.Text.RegularExpressions;

namespace SData.Tool.I18nScanner;

/// <summary>
/// 扫描到的业务消息.
/// </summary>
/// <param name="Key">消息 key.</param>
/// <param name="File">来源文件（相对路径）.</param>
public sealed record BusinessMessageEntry(string Key, string File);

/// <summary>
/// Roslyn-free 业务消息扫描器（正则匹配 BusinessException 的 message 字面量）.
/// </summary>
public sealed partial class BusinessMessageScanner
{
    /// <summary>
    /// 扫描目录.
    /// </summary>
    /// <param name="sourceDirectory">源码目录.</param>
    /// <returns>消息列表（按文件去重）.</returns>
    public IReadOnlyList<BusinessMessageEntry> Scan(string sourceDirectory)
    {
        var results = new Dictionary<string, BusinessMessageEntry>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var content = File.ReadAllText(file);
            var relative = Path.GetRelativePath(sourceDirectory, file);

            foreach (Match match in ThrowPattern().Matches(content))
            {
                var message = Unescape(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    results.TryAdd(message, new BusinessMessageEntry(message, relative));
                }
            }

            foreach (Match match in NewPattern().Matches(content))
            {
                var message = Unescape(match.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    results.TryAdd(message, new BusinessMessageEntry(message, relative));
                }
            }
        }

        return results.Values.ToList();
    }

    private static string Unescape(string value)
    {
        return value
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"BusinessException\.Throw\(\s*\d+\s*,\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex ThrowPattern();

    [GeneratedRegex(@"new BusinessException\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex NewPattern();
}
