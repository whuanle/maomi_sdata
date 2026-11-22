using System.Text.Json;

namespace SData.Ingest.Services;

/// <summary>简单 JSON 接入的字典序列化共享入口.</summary>
internal static class IngestJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    internal static string? Serialize(Dictionary<string, object?>? values)
    {
        return values == null || values.Count == 0
            ? null
            : JsonSerializer.Serialize(values, Options);
    }

    /// <summary>
    /// 原始 JSON 元素直通：取原始文本，避免字典化再回序列化的二跳开销.
    /// </summary>
    /// <param name="element">绑定后保留的原始元素.</param>
    /// <returns>JSON 文本；空/Undefined/Null 返回 null.</returns>
    internal static string? Serialize(JsonElement? element)
    {
        if (!element.HasValue)
        {
            return null;
        }

        return element.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : element.Value.GetRawText();
    }
}
