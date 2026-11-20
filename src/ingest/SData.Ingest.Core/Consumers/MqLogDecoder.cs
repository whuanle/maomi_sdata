using System.Text;
using System.Text.Json;
using SData.Infra.Configuration;
using SData.Store.Models;
using SData.Ingest.Services;

namespace SData.Ingest.Consumers;

/// <summary>
/// MQ 消息日志解码接口（实现在 Core，消费者调用）.
/// </summary>
public interface IMqLogDecoder
{
    /// <summary>
    /// 解码消息载荷为日志记录（按 topic 配置的解析规则分派）.
    /// </summary>
    /// <param name="payload">消息载荷.</param>
    /// <param name="topic">topic/队列解析规则（Format + Service/Severity 默认值 + Parser 参数）.</param>
    /// <param name="source">来源标记（写入记录的 Source，如 kafka:topic；空 = 不标记）.</param>
    /// <param name="records">解码结果（解析失败的行以原文入库并带 _parse_error 标记，不丢数据）.</param>
    /// <param name="error">首个解析失败原因（人类可读中文；全部成功为 null）.</param>
    /// <returns>true = 产生预览记录；false 或 error 非空时消费者必须阻塞重试，不得确认.</returns>
    bool TryDecode(byte[] payload, MqTopicOptions topic, string source, out List<LogRecord> records, out string? error);
}

/// <summary>
/// MQ 消息日志解码器：按 topic 配置的解析规则分派（otlp-proto/otlp-json/json/regex/csv/logfmt/syslog/raw 八种）.
/// 解析失败不丢数据：原文入 body、severity 用 topic 默认值、attributes 带 _parse_error 标记.
/// </summary>
public class MqLogDecoder : IMqLogDecoder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IOtlpProtoParser _protoParser;

    /// <summary>
    /// Initializes a new instance of the <see cref="MqLogDecoder"/> class.
    /// </summary>
    /// <param name="protoParser">OTLP proto 解析.</param>
    public MqLogDecoder(IOtlpProtoParser protoParser)
    {
        _protoParser = protoParser;
    }

    /// <inheritdoc/>
    public bool TryDecode(byte[] payload, MqTopicOptions topic, string source, out List<LogRecord> records, out string? error)
    {
        records = [];
        error = null;
        if (payload.Length == 0 || !MqLogFormat.IsSupported(topic.Format))
        {
            return false;
        }

        try
        {
            switch (topic.Format)
            {
                case MqLogFormat.OtlpProto:
                    records = _protoParser.ParseLogs(payload);
                    break;
                case MqLogFormat.OtlpJson:
                    records = OtlpLogParser.Parse(Encoding.UTF8.GetString(payload));
                    break;
                case MqLogFormat.Json:
                    records = ParseJsonPayload(Encoding.UTF8.GetString(payload), topic);
                    break;
                case MqLogFormat.Regex:
                    records = ParseLines(Encoding.UTF8.GetString(payload), topic, RegexLineParser.TryParse);
                    break;
                case MqLogFormat.Csv:
                    records = ParseLines(Encoding.UTF8.GetString(payload), topic, CsvLineParser.TryParse);
                    break;
                case MqLogFormat.Logfmt:
                    records = ParseLines(Encoding.UTF8.GetString(payload), topic, LogfmtLineParser.TryParse);
                    break;
                case MqLogFormat.Syslog:
                    records = ParseLines(Encoding.UTF8.GetString(payload), topic, SyslogLineParser.TryParse);
                    break;
                case MqLogFormat.Raw:
                    records = [BuildRawRecord(Encoding.UTF8.GetString(payload), topic)];
                    break;
                default:
                    return false;
            }
        }
        catch (Exception exception)
        {
            // 载荷与配置规则严重不符（如 proto 字节损坏、JSON 畸形）：原文入库不丢数据
            error = $"载荷解析失败：{exception.Message}";
            records = [BuildFallbackRecord(Encoding.UTF8.GetString(payload), topic, error)];
        }

        if (!string.IsNullOrEmpty(source))
        {
            foreach (var record in records)
            {
                record.Source = source;
            }
        }

        error ??= GetParseError(records.FirstOrDefault(record => record.AttributesJson?.Contains("_parse_error", StringComparison.Ordinal) == true));
        return records.Count > 0;
    }

    /// <summary>
    /// 文本格式逐行解析：未匹配行按失败策略处理；regex 格式的非首个未匹配行并入上一条正文（兼容异常堆栈续行）.
    /// </summary>
    private static List<LogRecord> ParseLines(string text, MqTopicOptions topic, TryParseLine tryParse)
    {
        var parser = topic.Format == MqLogFormat.Syslog ? SyslogLineParser.EffectiveParser(topic.Parser) : topic.Parser;
        var records = new List<LogRecord>();
        foreach (var line in SplitLines(text))
        {
            if (tryParse(line, parser, out var fields, out var lineError))
            {
                records.Add(TextLogMapping.MapFields(fields, topic, parser, line));
                continue;
            }

            if (topic.Format == MqLogFormat.Regex && records.Count > 0)
            {
                // 续行（如异常堆栈）：并入上一条记录正文，不产生独立失败行
                records[^1].Body += "\n" + line;
                continue;
            }

            records.Add(BuildFallbackRecord(line, topic, lineError));
        }

        return records;
    }

    /// <summary>
    /// 拆分多行消息（按 \n，容忍 \r；跳过空白行）.
    /// </summary>
    internal static IEnumerable<string> SplitLines(string text)
    {
        foreach (var part in text.Split('\n'))
        {
            var line = part.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// json 格式：单对象或对象数组；配置了字段映射走通用提取，否则兼容旧 IngestLogItem 形状.
    /// </summary>
    private static List<LogRecord> ParseJsonPayload(string text, MqTopicOptions topic)
    {
        var records = new List<LogRecord>();
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object && root.ValueKind != JsonValueKind.Array)
        {
            records.Add(BuildFallbackRecord(text, topic, "JSON 根节点不是对象或数组"));
            return records;
        }

        var useMapping = HasAnyMapping(topic.Parser);
        var elements = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        foreach (var element in elements)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                records.Add(BuildFallbackRecord(element.GetRawText(), topic, "JSON 元素不是对象"));
                continue;
            }

            if (useMapping)
            {
                records.Add(TextLogMapping.MapFields(TextLogMapping.FlattenElement(element), topic, topic.Parser, element.GetRawText()));
            }
            else if (TryMapLegacyItem(element, topic, out var record))
            {
                records.Add(record);
            }
            else
            {
                records.Add(BuildFallbackRecord(element.GetRawText(), topic, "JSON 形状与 IngestLogItem 不符且未配置字段映射"));
            }
        }

        return records;
    }

    /// <summary>
    /// 是否配置了任意字段映射（json 格式走通用提取的开关）.
    /// </summary>
    private static bool HasAnyMapping(MqParserOptions? parser)
    {
        return parser != null
            && (parser.TimestampField.Length > 0
                || parser.SeverityField.Length > 0
                || parser.ServiceField.Length > 0
                || parser.BodyField.Length > 0
                || parser.TraceIdField.Length > 0
                || parser.SpanIdField.Length > 0);
    }

    /// <summary>
    /// 单个 JSON 元素 → IngestLogItem → LogRecord（兼容 POST /ingest/logs 形状）；形状不符返回 false.
    /// </summary>
    private static bool TryMapLegacyItem(JsonElement element, MqTopicOptions topic, out LogRecord record)
    {
        record = new LogRecord();
        IngestLogItem? item;
        try
        {
            // Web 默认（camelCase + 大小写不敏感），与 POST /ingest/logs 的模型绑定行为一致
            item = JsonSerializer.Deserialize<IngestLogItem>(element.GetRawText(), JsonOptions);
        }
        catch
        {
            return false;
        }

        if (item == null || element.ValueKind != JsonValueKind.Object
            || !element.EnumerateObject().Any(property => string.Equals(property.Name, "body", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        record = IngestItemMapper.ToLogRecord(item);
        if (!string.IsNullOrEmpty(topic.Service))
        {
            record.Service = topic.Service;
        }

        return true;
    }

    /// <summary>
    /// raw 格式：整条消息为正文；行首 RFC3339 时间戳（含时间部分，避免误吞纯日期前缀）可被识别提取，其余为正文.
    /// </summary>
    private static LogRecord BuildRawRecord(string line, MqTopicOptions topic)
    {
        var body = line;
        var timestamp = DateTimeOffset.UtcNow;
        var spaceIndex = body.IndexOfAny([' ', '\t']);
        var candidate = spaceIndex > 0 ? body[..spaceIndex] : string.Empty;
        if (spaceIndex > 0 && (candidate.Contains('T') || candidate.Contains(':')) && DateTimeOffset.TryParse(
                candidate,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            timestamp = parsed;
            body = body[(spaceIndex + 1)..];
        }

        return new LogRecord
        {
            Timestamp = timestamp,
            Service = string.IsNullOrWhiteSpace(topic.Service) ? "unknown" : topic.Service,
            Severity = TextLogMapping.NormalizeSeverity(topic.Severity, "INFO"),
            Body = body,
        };
    }

    /// <summary>
    /// 失败策略记录：原文入 body、severity/service 用 topic 默认值、attributes 带 _parse_error 标记（不丢数据）.
    /// </summary>
    internal static LogRecord BuildFallbackRecord(string raw, MqTopicOptions topic, string? reason)
    {
        var attributes = JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_parse_error"] = string.IsNullOrEmpty(reason) ? "解析失败" : reason,
        }, JsonOptions);
        return new LogRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            Service = string.IsNullOrWhiteSpace(topic.Service) ? "unknown" : topic.Service,
            Severity = TextLogMapping.NormalizeSeverity(topic.Severity, "INFO"),
            Body = raw,
            AttributesJson = attributes,
        };
    }

    /// <summary>
    /// 从记录 attributes 提取 _parse_error 标记值（记录为空或无标记则 null）.
    /// </summary>
    internal static string? GetParseError(LogRecord? record)
    {
        if (record == null || string.IsNullOrEmpty(record.AttributesJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(record.AttributesJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("_parse_error", out var element)
                && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
