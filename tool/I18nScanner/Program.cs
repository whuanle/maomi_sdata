using System.Text.Json;
using SData.Tool.I18nScanner;

// I18nScanner：扫描 src/ 下的 BusinessException 消息，生成/合并 i18n 资源 JSON。
// 用法：dotnet run --project tool/I18nScanner -- [--src <dir>] [--out <dir>] [--dry-run] [--report <file>]
var options = ScanOptions.Parse(args);

if (!Directory.Exists(options.SourceDirectory))
{
    Console.Error.WriteLine($"source directory not found: {options.SourceDirectory}");
    return 1;
}

var scanner = new BusinessMessageScanner();
var entries = scanner.Scan(options.SourceDirectory);
Console.WriteLine($"scanned {entries.Count} business messages");

Directory.CreateDirectory(options.OutputDirectory);

var zhPath = Path.Combine(options.OutputDirectory, "zh-CN.json");
var enPath = Path.Combine(options.OutputDirectory, "en-US.json");

if (options.DryRun)
{
    foreach (var entry in entries.OrderBy(x => x.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"  {entry.Key}");
    }

    return 0;
}

var merger = new ResourceMerger();
merger.Merge(zhPath, entries);
merger.Merge(enPath, entries, fallbackToEmptyTranslation: true);
Console.WriteLine($"written: {zhPath}, {enPath}");

if (!string.IsNullOrEmpty(options.ReportPath))
{
    var report = new
    {
        generatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
        count = entries.Count,
        keys = entries.Select(x => x.Key).OrderBy(x => x).ToList(),
    };
    File.WriteAllText(options.ReportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"report: {options.ReportPath}");
}

return 0;

namespace SData.Tool.I18nScanner
{
    /// <summary>
    /// 扫描参数.
    /// </summary>
    public class ScanOptions
    {
        /// <summary>源码目录.</summary>
        public string SourceDirectory { get; set; } = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src"));

        /// <summary>输出目录.</summary>
        public string OutputDirectory { get; set; } = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "SData", "i18n"));

        /// <summary>仅打印不写入.</summary>
        public bool DryRun { get; set; }

        /// <summary>报告文件.</summary>
        public string? ReportPath { get; set; }

        /// <summary>
        /// 解析命令行参数.
        /// </summary>
        /// <param name="args">参数.</param>
        /// <returns>选项.</returns>
        public static ScanOptions Parse(string[] args)
        {
            var options = new ScanOptions();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--src":
                        options.SourceDirectory = Path.GetFullPath(args[++i]);
                        break;
                    case "--out":
                        options.OutputDirectory = Path.GetFullPath(args[++i]);
                        break;
                    case "--dry-run":
                        options.DryRun = true;
                        break;
                    case "--report":
                        options.ReportPath = args[++i];
                        break;
                }
            }

            return options;
        }
    }
}
