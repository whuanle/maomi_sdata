using System.Text.Encodings.Web;
using System.Text.Json;

namespace SData.Tool.I18nScanner;

/// <summary>
/// i18n 资源合并器：保留已有翻译，新增 key 用原文兜底，删除已不存在的 key.
/// </summary>
public sealed class ResourceMerger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 合并并写入资源文件.
    /// </summary>
    /// <param name="path">资源文件路径.</param>
    /// <param name="entries">消息列表.</param>
    /// <param name="fallbackToEmptyTranslation">无翻译时是否用空串占位（en-US 用）.</param>
    public void Merge(string path, IReadOnlyList<BusinessMessageEntry> entries, bool fallbackToEmptyTranslation = false)
    {
        var existing = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            var document = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonOptions);
            if (document != null)
            {
                foreach (var (key, value) in document)
                {
                    existing[key] = value;
                }
            }
        }

        var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            // 已有翻译优先保留；en-US 用原文占位，后续人工翻译
            merged[entry.Key] = existing.TryGetValue(entry.Key, out var value) ? value : entry.Key;
        }

        File.WriteAllText(path, JsonSerializer.Serialize(merged, JsonOptions) + "\n", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
