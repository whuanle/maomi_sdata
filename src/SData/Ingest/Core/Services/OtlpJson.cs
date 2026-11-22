using System.Text.Json;

namespace SData.Ingest.Services;

/// <summary>
/// OTLP/HTTP(JSON) 解析公共工具.
/// </summary>
public static class OtlpJson
{
    /// <summary>
    /// 读取 kvlist 属性为 JSON 对象字符串.
    /// </summary>
    /// <param name="element">OTLP 属性数组（{key, value:{stringValue|boolValue|intValue|doubleValue|bytesValue|arrayValue|kvlistValue}}）.</param>
    /// <returns>JSON 对象字符串；空返回 null.</returns>
    public static string? AttributesToJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var attribute in element.EnumerateArray())
            {
                var key = attribute.TryGetProperty("key", out var keyElement) ? keyElement.GetString() : null;
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (!attribute.TryGetProperty("value", out var valueElement))
                {
                    writer.WriteNull(key);
                    continue;
                }

                WriteAnyValue(writer, key, valueElement);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// 解析纳秒时间戳（OTLP JSON 中为字符串或数字）.
    /// </summary>
    /// <param name="element">时间戳元素.</param>
    /// <returns>毫秒时间戳；解析失败返回 null.</returns>
    public static long? UnixNanoToMs(JsonElement element)
    {
        long nano;
        if (element.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(element.GetString(), out nano))
            {
                return null;
            }
        }
        else if (element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetInt64(out nano))
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        return nano / 1_000_000;
    }

    /// <summary>解析纳秒时间戳为微秒，供链路时间轴使用.</summary>
    public static long? UnixNanoToUs(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out var textValue))
        {
            return textValue / 1000;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number) ? number / 1000 : null;
    }

    /// <summary>
    /// 从资源 JSON 中提取 service.name.
    /// </summary>
    /// <param name="resourceJson">资源 JSON 对象字符串.</param>
    /// <returns>服务名.</returns>
    public static string ExtractServiceName(string? resourceJson)
    {
        if (string.IsNullOrEmpty(resourceJson))
        {
            return "unknown";
        }

        try
        {
            using var document = JsonDocument.Parse(resourceJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("service.name", out var service)
                && service.ValueKind == JsonValueKind.String)
            {
                return service.GetString() ?? "unknown";
            }
        }
        catch (JsonException)
        {
        }

        return "unknown";
    }

    private static void WriteAnyValue(Utf8JsonWriter writer, string key, JsonElement value)
    {
        if (value.TryGetProperty("stringValue", out var stringValue))
        {
            writer.WriteString(key, stringValue.GetString());
            return;
        }

        if (value.TryGetProperty("boolValue", out var boolValue) && boolValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            writer.WriteBoolean(key, boolValue.GetBoolean());
            return;
        }

        if (value.TryGetProperty("intValue", out var intValue))
        {
            if (intValue.ValueKind == JsonValueKind.String)
            {
                writer.WriteString(key, intValue.GetString());
            }
            else if (intValue.TryGetInt64(out var intValue64))
            {
                writer.WriteNumber(key, intValue64);
            }
            else
            {
                writer.WriteNumber(key, intValue.GetDouble());
            }

            return;
        }

        if (value.TryGetProperty("doubleValue", out var doubleValue))
        {
            writer.WriteNumber(key, doubleValue.GetDouble());
            return;
        }

        if (value.TryGetProperty("bytesValue", out var bytesValue) && bytesValue.ValueKind == JsonValueKind.String)
        {
            writer.WriteString(key, bytesValue.GetString());
            return;
        }

        if (value.TryGetProperty("arrayValue", out var arrayValue) && arrayValue.TryGetProperty("values", out var arrayValues))
        {
            writer.WriteStartArray(key);
            foreach (var item in arrayValues.EnumerateArray())
            {
                WriteItem(writer, item);
            }

            writer.WriteEndArray();
            return;
        }

        if (value.TryGetProperty("kvlistValue", out var kvlistValue) && kvlistValue.TryGetProperty("values", out var kvValues))
        {
            writer.WriteStartObject(key);
            foreach (var attribute in kvValues.EnumerateArray())
            {
                var childKey = attribute.TryGetProperty("key", out var childKeyElement) ? childKeyElement.GetString() : null;
                if (!string.IsNullOrEmpty(childKey))
                {
                    if (attribute.TryGetProperty("value", out var childValue))
                    {
                        WriteAnyValue(writer, childKey, childValue);
                    }
                    else
                    {
                        writer.WriteNull(childKey);
                    }
                }
            }

            writer.WriteEndObject();
            return;
        }

        writer.WriteNull(key);
    }

    private static void WriteItem(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.TryGetProperty("stringValue", out var stringValue))
        {
            writer.WriteStringValue(stringValue.GetString());
            return;
        }

        if (value.TryGetProperty("boolValue", out var boolValue) && boolValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            writer.WriteBooleanValue(boolValue.GetBoolean());
            return;
        }

        if (value.TryGetProperty("intValue", out var intValue))
        {
            if (intValue.ValueKind == JsonValueKind.String)
            {
                writer.WriteStringValue(intValue.GetString());
            }
            else if (intValue.TryGetInt64(out var intValue64))
            {
                writer.WriteNumberValue(intValue64);
            }
            else
            {
                writer.WriteNumberValue(intValue.GetDouble());
            }

            return;
        }

        if (value.TryGetProperty("doubleValue", out var doubleValue))
        {
            writer.WriteNumberValue(doubleValue.GetDouble());
            return;
        }

        writer.WriteNullValue();
    }
}
