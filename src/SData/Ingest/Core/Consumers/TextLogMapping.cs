using System.Globalization;
using System.Text.Json;
using SData.Infra.Configuration;
using SData.Store.Models;

namespace SData.Ingest.Consumers;

/// <summary>
/// 文本日志公共映射层：字段字典（regex 命名捕获组 / csv 列索引 / logfmt 键 / json 展平路径）
/// → 按 MqParserOptions 映射 → LogRecord（时间解析、级别别名归一、未映射字段进 attributes）.
/// </summary>
public static class TextLogMapping
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 级别别名归一：自定义映射优先，再查内置别名表（大小写不敏感），数值 0-7 按 syslog 优先级映射，未知值原样大写放行.
    /// </summary>
    /// <param name="value">原始级别值.</param>
    /// <param name="fallback">取不到值时的默认级别.</param>
    /// <param name="customMap">配置的别名映射（优先于内置表）.</param>
    /// <returns>标准级别（TRACE/DEBUG/INFO/WARN/ERROR/FATAL 或原样大写）.</returns>
    public static string NormalizeSeverity(string? value, string fallback, Dictionary<string, string>? customMap = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return NormalizeStandard(fallback);
        }

        var trimmed = value.Trim();
        if (customMap != null)
        {
            foreach (var pair in customMap)
            {
                if (string.Equals(pair.Key, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeStandard(pair.Value ?? string.Empty);
                }
            }
        }

        return NormalizeStandard(trimmed);
    }

    /// <summary>
    /// 内置级别别名归一.
    /// </summary>
    private static string NormalizeStandard(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "INFO";
        }

        if (value.Length == 1 && char.IsDigit(value[0]))
        {
            return int.Parse(value, CultureInfo.InvariantCulture) switch
            {
                0 => "FATAL",
                1 or 2 or 3 => "ERROR",
                4 => "WARN",
                5 or 6 => "INFO",
                _ => "DEBUG",
            };
        }

        return value.ToLowerInvariant() switch
        {
            "trace" or "t" or "finest" or "finer" => "TRACE",
            "debug" or "d" or "verbose" => "DEBUG",
            "info" or "i" or "information" or "notice" => "INFO",
            "warn" or "w" or "warning" => "WARN",
            "error" or "e" or "err" => "ERROR",
            "critical" or "crit" or "c" or "alert" => "ERROR",
            "fatal" or "f" or "emerg" or "emergency" or "panic" => "FATAL",
            _ => value.ToUpperInvariant(),
        };
    }

    /// <summary>
    /// 时间解析：自定义格式串（DateTimeOffset.ParseExact）→ 自动嗅探（DateTimeOffset.Parse 兼容 RFC3339/ISO/常用格式，含偏移量按偏移转 UTC、无偏移按服务器本地时区）→ 纯数字按 epoch（≥12 位毫秒，否则秒）→ 全部失败回退 fallback.
    /// </summary>
    /// <param name="value">时间字符串.</param>
    /// <param name="format">自定义格式串（空 = 自动嗅探）.</param>
    /// <param name="fallback">解析失败时的兜底时间.</param>
    /// <returns>UTC 时间.</returns>
    public static DateTimeOffset ParseTimestamp(string? value, string? format, DateTimeOffset fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        if (!string.IsNullOrEmpty(format))
        {
            if (DateTimeOffset.TryParseExact(
                    trimmed,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal,
                    out var exact))
            {
                return exact;
            }

            return fallback;
        }

        if (trimmed.All(char.IsDigit))
        {
            return trimmed.Length >= 12
                ? DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(trimmed, CultureInfo.InvariantCulture))
                : DateTimeOffset.FromUnixTimeSeconds(long.Parse(trimmed, CultureInfo.InvariantCulture));
        }

        if (DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    /// <summary>
    /// 字段字典 + 映射配置 → LogRecord（未配置字段用 topic 默认值；KeepUnmapped 时剩余字段进 attributes）.
    /// </summary>
    /// <param name="fields">提取的字段字典（键 = 字段名/捕获组名/列索引）.</param>
    /// <param name="topic">topic 解析规则（Service/Severity 默认值）.</param>
    /// <param name="parser">生效的解析参数（syslog 为补齐默认映射后的副本）.</param>
    /// <param name="rawLine">整行原文（BodyField 未配置时为正文）.</param>
    /// <returns>日志记录.</returns>
    public static LogRecord MapFields(Dictionary<string, string> fields, MqTopicOptions topic, MqParserOptions? parser, string rawLine)
    {
        string? GetField(string? field) => !string.IsNullOrEmpty(field) && fields.TryGetValue(field, out var v) ? v : null;

        var body = GetField(parser?.BodyField);
        if (string.IsNullOrEmpty(body))
        {
            body = rawLine;
        }

        var service = GetField(parser?.ServiceField);
        if (string.IsNullOrWhiteSpace(service))
        {
            service = string.IsNullOrWhiteSpace(topic.Service) ? "unknown" : topic.Service;
        }

        return new LogRecord
        {
            Timestamp = ParseTimestamp(GetField(parser?.TimestampField), parser?.TimestampFormat, DateTimeOffset.UtcNow),
            Service = service,
            Severity = NormalizeSeverity(GetField(parser?.SeverityField), topic.Severity, parser?.SeverityMap),
            Body = body,
            TraceId = GetField(parser?.TraceIdField),
            SpanId = GetField(parser?.SpanIdField),
            AttributesJson = BuildAttributes(fields, parser),
        };
    }

    /// <summary>
    /// 未映射字段序列化为 attributes JSON（KeepUnmapped=false 或无剩余字段时 null）.
    /// </summary>
    private static string? BuildAttributes(Dictionary<string, string> fields, MqParserOptions? parser)
    {
        if (parser is { KeepUnmapped: false })
        {
            return null;
        }

        var mapped = new HashSet<string>(StringComparer.Ordinal);
        CollectIfSet(mapped, parser?.TimestampField);
        CollectIfSet(mapped, parser?.SeverityField);
        CollectIfSet(mapped, parser?.ServiceField);
        CollectIfSet(mapped, parser?.BodyField);
        CollectIfSet(mapped, parser?.TraceIdField);
        CollectIfSet(mapped, parser?.SpanIdField);
        var unmapped = fields
            .Where(pair => !mapped.Contains(pair.Key) && !string.IsNullOrEmpty(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return unmapped.Count > 0 ? JsonSerializer.Serialize(unmapped, JsonOptions) : null;
    }

    /// <summary>
    /// JSON 元素展平为字段字典（嵌套对象用点路径，如 data.message；数组整体取原文字符串；null 跳过）.
    /// </summary>
    /// <param name="element">JSON 对象元素.</param>
    /// <returns>字段字典.</returns>
    public static Dictionary<string, string> FlattenElement(JsonElement element)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        Flatten(element, string.Empty, fields);
        return fields;
    }

    /// <summary>
    /// 递归展平.
    /// </summary>
    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> fields)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}", fields);
                }

                break;
            case JsonValueKind.Array:
                fields[prefix] = element.GetRawText();
                break;
            case JsonValueKind.String:
                fields[prefix] = element.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                fields[prefix] = element.GetRawText();
                break;
        }
    }

    private static void CollectIfSet(HashSet<string> mapped, string? field)
    {
        if (!string.IsNullOrEmpty(field))
        {
            mapped.Add(field);
        }
    }
}
