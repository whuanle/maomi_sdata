using System.Globalization;
using System.Text;
using System.Text.Json;
using SData.Store.Models;

namespace SData.Iot.Mqtt;

/// <summary>
/// MQTT 遥测主题与消息体协议（单值与批量 JSON 两形态，消息体一律为合法 JSON）.
/// 主题约定：
///   iot/{deviceKey}/{signal}          —— 单值：消息体为 JSON 对象、JSON 数字或 JSON 字符串
///   iot/{deviceKey}/（任意子主题）     —— 批量：消息体为 JSON 数组（元素自带 signal）
/// 消息体字段约定：v|value = 值（数值→NumValue、字符串→StrValue，必填）；
/// s|signal = 信号名（批量必填）；ts|time = 时间戳（ISO8601 字符串或 epoch 毫秒数，缺省取接收时间）；
/// q|quality = 质量码（int，缺省 0）.
/// </summary>
public static class MqttTopicProtocol
{
    /// <summary>
    /// 遥测主题根前缀.
    /// </summary>
    public const string TopicPrefix = "iot";

    /// <summary>
    /// 解析主题为设备标识与信号名.
    /// </summary>
    /// <param name="topic">MQTT 主题.</param>
    /// <param name="deviceKey">设备标识.</param>
    /// <param name="signal">信号名（多级以 / 连接）.</param>
    /// <returns>是否合法.</returns>
    public static bool TryParseTopic(string? topic, out string deviceKey, out string signal)
    {
        deviceKey = string.Empty;
        signal = string.Empty;
        if (string.IsNullOrEmpty(topic))
        {
            return false;
        }

        var segments = topic.Split('/');
        if (segments.Length < 3 || segments[0] != TopicPrefix)
        {
            return false;
        }

        if (segments[1].Length == 0 || segments[2].Length == 0)
        {
            return false;
        }

        deviceKey = segments[1];
        signal = string.Join('/', segments, 2, segments.Length - 2);
        return true;
    }

    /// <summary>
    /// 解析消息体为遥测记录（单值与批量 JSON 两形态）.
    /// </summary>
    /// <param name="topic">MQTT 主题（用于单值形态提取信号名与设备标识）.</param>
    /// <param name="payload">消息体字节.</param>
    /// <param name="fallbackTime">消息体缺省时间戳时使用的接收时间.</param>
    /// <param name="records">解析出的遥测记录.</param>
    /// <param name="error">失败原因（成功为 null）.</param>
    /// <returns>是否解析成功.</returns>
    public static bool TryParsePayload(string topic, byte[] payload, DateTimeOffset fallbackTime, out List<TelemetryRecord> records, out string? error)
    {
        records = new List<TelemetryRecord>();
        error = null;
        if (payload.Length == 0)
        {
            error = "消息体为空";
            return false;
        }

        if (!TryParseTopic(topic, out var deviceKey, out var topicSignal))
        {
            error = $"主题不合法: {topic}";
            return false;
        }

        var text = Encoding.UTF8.GetString(payload);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            error = $"消息体不是合法 JSON: {exception.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.Array:
                    return TryParseBatch(deviceKey, root, fallbackTime, records, out error);
                case JsonValueKind.Object:
                    if (TryParseSingle(deviceKey, topicSignal, root, fallbackTime, records, out error))
                    {
                        return true;
                    }

                    return false;
                case JsonValueKind.Number:
                    records.Add(new TelemetryRecord
                    {
                        DeviceKey = deviceKey,
                        Signal = topicSignal,
                        Timestamp = fallbackTime,
                        NumValue = root.GetDouble(),
                    });
                    return true;
                case JsonValueKind.String:
                    var strValue = root.GetString();
                    if (string.IsNullOrEmpty(strValue))
                    {
                        error = "字符串消息体为空";
                        return false;
                    }

                    records.Add(new TelemetryRecord
                    {
                        DeviceKey = deviceKey,
                        Signal = topicSignal,
                        Timestamp = fallbackTime,
                        StrValue = strValue,
                    });
                    return true;
                default:
                    error = $"不支持的消息体形态: {root.ValueKind}";
                    return false;
            }
        }
    }

    private static bool TryParseBatch(string deviceKey, JsonElement root, DateTimeOffset fallbackTime, List<TelemetryRecord> records, out string? error)
    {
        error = null;
        var index = 0;
        foreach (var item in root.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = $"批量第 {index} 项不是 JSON 对象";
                return false;
            }

            var signal = GetStringField(item, "s", "signal");
            if (string.IsNullOrEmpty(signal))
            {
                error = $"批量第 {index} 项缺少 s/signal 字段";
                return false;
            }

            if (!TryBuildRecord(deviceKey, signal, item, fallbackTime, $"批量第 {index} 项", records, out error))
            {
                return false;
            }
        }

        if (records.Count == 0)
        {
            error = "批量数组为空";
            return false;
        }

        return true;
    }

    private static bool TryParseSingle(string deviceKey, string signal, JsonElement root, DateTimeOffset fallbackTime, List<TelemetryRecord> records, out string? error)
    {
        error = null;
        return TryBuildRecord(deviceKey, signal, root, fallbackTime, "消息体", records, out error);
    }

    private static bool TryBuildRecord(string deviceKey, string signal, JsonElement element, DateTimeOffset fallbackTime, string errorPrefix, List<TelemetryRecord> records, out string? error)
    {
        error = null;
        if (!TryReadValue(element, out var numValue, out var strValue, out error))
        {
            error = $"{errorPrefix}: {error}";
            return false;
        }

        var timestamp = fallbackTime;
        var timestampRaw = GetField(element, "ts", "time");
        if (timestampRaw.ValueKind != JsonValueKind.Undefined)
        {
            if (timestampRaw.ValueKind == JsonValueKind.Number)
            {
                if (!timestampRaw.TryGetInt64(out var epochMs))
                {
                    error = $"{errorPrefix}: ts/time 必须是 epoch 毫秒整数";
                    return false;
                }

                timestamp = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
            }
            else if (timestampRaw.ValueKind == JsonValueKind.String)
            {
                if (!DateTimeOffset.TryParse(timestampRaw.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp))
                {
                    error = $"{errorPrefix}: ts/time 必须是 ISO8601 时间";
                    return false;
                }
            }
            else
            {
                error = $"{errorPrefix}: ts/time 必须是 ISO8601 字符串或 epoch 毫秒数";
                return false;
            }
        }

        var quality = 0;
        var qualityRaw = GetField(element, "q", "quality");
        if (qualityRaw.ValueKind != JsonValueKind.Undefined && (!qualityRaw.TryGetInt32(out quality) || quality < 0))
        {
            error = $"{errorPrefix}: q/quality 必须是非负整数";
            return false;
        }

        records.Add(new TelemetryRecord
        {
            DeviceKey = deviceKey,
            Signal = signal,
            Timestamp = timestamp,
            NumValue = numValue,
            StrValue = strValue,
            Quality = quality,
        });
        return true;
    }

    private static bool TryReadValue(JsonElement element, out double? numValue, out string? strValue, out string? error)
    {
        numValue = null;
        strValue = null;
        error = null;
        var valueRaw = GetField(element, "v", "value");
        if (valueRaw.ValueKind == JsonValueKind.Undefined)
        {
            error = "缺少 v/value 字段";
            return false;
        }

        switch (valueRaw.ValueKind)
        {
            case JsonValueKind.Number:
                numValue = valueRaw.GetDouble();
                return true;
            case JsonValueKind.String:
                var value = valueRaw.GetString();
                if (string.IsNullOrEmpty(value))
                {
                    error = "v/value 字符串为空";
                    return false;
                }

                strValue = value;
                return true;
            default:
                error = "v/value 必须是数值或字符串";
                return false;
        }
    }

    private static JsonElement GetField(JsonElement element, string primary, string alias)
    {
        if (element.TryGetProperty(primary, out var value))
        {
            return value;
        }

        if (element.TryGetProperty(alias, out var aliased))
        {
            return aliased;
        }

        return default;
    }

    private static string? GetStringField(JsonElement element, string primary, string alias)
    {
        var raw = GetField(element, primary, alias);
        return raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
    }
}
