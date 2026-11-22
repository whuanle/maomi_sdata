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
}
