using System.Globalization;
using System.Text.Json;

namespace SData.Ingest.Services;

/// <summary>
/// Elasticsearch _bulk NDJSON 解析器（静态实现）：仅支持 index/create 动作写入日志，
/// update/delete 返回 ES 形状错误项；坏行单项失败不影响其余项.
/// </summary>
public static class EsBulkParser
{
    /// <summary>
    /// 解析 NDJSON 请求体（动作行 + 文档行成对，delete 动作无文档行）.
    /// </summary>
    /// <param name="ndjson">请求体原文.</param>
    /// <param name="urlIndex">URL /&#123;index&#125;/_bulk 中的默认索引.</param>
    /// <returns>解析结果.</returns>
    public static EsBulkParseResult Parse(string ndjson, string? urlIndex = null)
    {
        var result = new EsBulkParseResult();
        EsBulkItem? pending = null;
        foreach (var rawLine in ndjson.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (pending == null)
            {
                pending = ParseActionLine(result, line, urlIndex);
            }
            else
            {
                CompleteWithDocument(pending, line);
                pending = null;
            }
        }

        if (pending != null)
        {
            Fail(pending, "illegal_argument_exception", "动作行缺少文档行");
        }

        return result;
    }

    /// <summary>
    /// 逐条入缓冲（控制器 429 背压决策的纯函数形态）.
    /// </summary>
    /// <param name="result">解析结果.</param>
    /// <param name="enqueue">单条入缓冲委托（false = 缓冲满）.</param>
    /// <returns>false = 任一条缓冲满，整批应拒绝（HTTP 429）.</returns>
    public static bool EnqueueAll(EsBulkParseResult result, Func<SData.Store.Models.LogRecord, bool> enqueue)
    {
        foreach (var item in result.Items)
        {
            if (item.Success && !enqueue(item.Record!))
            {
                return false;
            }
        }

        return true;
    }

    private static EsBulkItem ParseActionLine(EsBulkParseResult result, string line, string? urlIndex)
    {
        var item = new EsBulkItem();
        result.Items.Add(item);

        JsonDocument meta;
        try
        {
            meta = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            Fail(item, "parse_exception", "动作行不是有效 JSON");
            return item;
        }

        using (meta)
        {
            var root = meta.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Fail(item, "parse_exception", "动作行必须是 JSON 对象");
                return item;
            }

            var hasKnownAction = false;
            foreach (var property in root.EnumerateObject())
            {
                var action = property.Name.ToLowerInvariant();
                if (action is not ("index" or "create" or "update" or "delete"))
                {
                    continue;
                }

                item.Action = action;
                hasKnownAction = true;
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    if (property.Value.TryGetProperty("_index", out var indexElement) && indexElement.ValueKind == JsonValueKind.String)
                    {
                        item.Index = indexElement.GetString() ?? string.Empty;
                    }

                    if (property.Value.TryGetProperty("_id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                    {
                        item.DocumentId = idElement.GetString();
                    }
                }

                break;
            }

            if (!hasKnownAction)
            {
                Fail(item, "illegal_argument_exception", "动作行缺少 index/create/update/delete 动作");
                return item;
            }

            if (string.IsNullOrEmpty(item.Index))
            {
                item.Index = urlIndex ?? string.Empty;
            }

            if (string.IsNullOrEmpty(item.Index))
            {
                Fail(item, "illegal_argument_exception", "缺少目标索引（_index 或 URL /{index}/_bulk）");
            }

            if (item.Action == "delete")
            {
                // delete 无文档行，直接按不支持动作处理
                Fail(item, "illegal_argument_exception", "仅支持 index/create 动作（写入兼容）");
                return null;
            }

            if (!item.Success && item.ErrorType != null)
            {
                // 已失败（update/缺索引等）但需消费文档行以保持配对
                return item;
            }

            if (item.Action is "index" or "create")
            {
                // 合法写入动作：等待下一文档行
                return item;
            }

            // update：标记不支持动作并消费其 doc 行
            Fail(item, "illegal_argument_exception", "仅支持 index/create 动作（写入兼容）");
            return item;
        }
    }

    private static void CompleteWithDocument(EsBulkItem item, string line)
    {
        if (!item.Success && item.ErrorType != null)
        {
            // 已失败（update/缺索引等），仅消费文档行
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            Fail(item, "parse_exception", "文档行不是有效 JSON");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Fail(item, "parse_exception", "文档行必须是 JSON 对象");
                return;
            }

            item.Record = new SData.Store.Models.LogRecord
            {
                Timestamp = TryGetTimestamp(root) ?? DateTimeOffset.UtcNow,
                Service = string.IsNullOrEmpty(item.Index) ? "unknown" : item.Index,
                Severity = GetSeverity(root),
                Body = root.GetRawText(),
                TraceId = TryGetString(root, "trace.id"),
                SpanId = TryGetString(root, "span.id"),
                Source = item.Index,
                AttributesJson = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_index"] = item.Index,
                    ["_id"] = item.DocumentId ?? string.Empty,
                }),
                ResourceJson = null,
            };
            item.Success = true;
        }
    }

    private static DateTimeOffset? TryGetTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("@timestamp", out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return parsed;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var unixMs))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        }

        return null;
    }

    private static string GetSeverity(JsonElement root)
    {
        var value = TryGetString(root, "log.level")
            ?? TryGetString(root, "level")
            ?? TryGetString(root, "severity");
        if (string.IsNullOrWhiteSpace(value)
            && root.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Object
            && log.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.String)
        {
            value = level.GetString();
        }

        return string.IsNullOrWhiteSpace(value) ? "INFO" : value.Trim().ToUpperInvariant();
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return null;
    }

    private static void Fail(EsBulkItem item, string errorType, string reason)
    {
        item.Success = false;
        item.Record = null;
        item.ErrorType = errorType;
        item.ErrorReason = reason;
    }
}

/// <summary>
/// IEsBulkParser 实现（包装静态解析器）.
/// </summary>
public class EsBulkParserService : IEsBulkParser
{
    /// <inheritdoc/>
    public EsBulkParseResult Parse(string ndjson, string? urlIndex = null) => EsBulkParser.Parse(ndjson, urlIndex);

    /// <inheritdoc/>
    public bool EnqueueAll(EsBulkParseResult result, Func<SData.Store.Models.LogRecord, bool> enqueue) => EsBulkParser.EnqueueAll(result, enqueue);
}
