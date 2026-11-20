namespace SData.Ingest;

/// <summary>
/// ES Bulk NDJSON 解析结果（逐项成败，坏行单项失败不影响其余项）.
/// </summary>
public class EsBulkParseResult
{
    /// <summary>
    /// 解析项（与动作行顺序一致）.
    /// </summary>
    public List<EsBulkItem> Items { get; } = new();

    /// <summary>
    /// 是否存在失败项（对应 ES 响应 errors:true）.
    /// </summary>
    public bool HasFailure
    {
        get
        {
            foreach (var item in Items)
            {
                if (!item.Success)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// 单个 bulk 动作的解析项.
/// </summary>
public class EsBulkItem
{
    /// <summary>
    /// 动作名（index/create/update/delete；动作行无法解析时为 index 占位）.
    /// </summary>
    public string Action { get; set; } = "index";

    /// <summary>
    /// 目标索引（_index；缺省时由 URL /&#123;index&#125;/_bulk 兜底）.
    /// </summary>
    public string Index { get; set; } = string.Empty;

    /// <summary>
    /// 文档 id（_id）.
    /// </summary>
    public string? DocumentId { get; set; }

    /// <summary>
    /// 解析成功（index/create 动作且文档 JSON 有效）.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 产出的日志记录（Success = true 时有值）.
    /// </summary>
    public SData.Store.Models.LogRecord? Record { get; set; }

    /// <summary>
    /// ES 错误类型（失败时）.
    /// </summary>
    public string? ErrorType { get; set; }

    /// <summary>
    /// ES 错误原因（失败时）.
    /// </summary>
    public string? ErrorReason { get; set; }
}

/// <summary>
/// Elasticsearch _bulk 响应（ES 形状：took/errors/items，兼容 Filebeat 响应解析）.
/// </summary>
public class EsBulkResponse
{
    /// <summary>
    /// 耗时（毫秒）.
    /// </summary>
    public long Took { get; set; }

    /// <summary>
    /// 是否存在失败项.
    /// </summary>
    public bool Errors { get; set; }

    /// <summary>
    /// 是否超时（写入兼容恒为 false）.
    /// </summary>
    public bool TimedOut => false;

    /// <summary>
    /// 逐项结果（每项形如 &#123;"index": &#123;"status": 201, ... &#125;&#125;）.
    /// </summary>
    public List<Dictionary<string, object?>> Items { get; } = new();

    /// <summary>
    /// 构造成功项（index/create）.
    /// </summary>
    /// <param name="action">动作名.</param>
    /// <param name="index">索引名.</param>
    /// <param name="documentId">文档 id.</param>
    /// <returns>ES 形状单项.</returns>
    public static Dictionary<string, object?> SuccessItem(string action, string index, string? documentId)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [action] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = 201,
                ["_index"] = index,
                ["_id"] = documentId ?? string.Empty,
            },
        };
    }

    /// <summary>
    /// 构造失败项（update/delete/坏行）.
    /// </summary>
    /// <param name="action">动作名.</param>
    /// <param name="index">索引名.</param>
    /// <param name="documentId">文档 id.</param>
    /// <param name="errorType">ES 错误类型.</param>
    /// <param name="reason">错误原因.</param>
    /// <returns>ES 形状单项.</returns>
    public static Dictionary<string, object?> ErrorItem(string action, string index, string? documentId, string errorType, string reason)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [action] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = 400,
                ["_index"] = index,
                ["_id"] = documentId ?? string.Empty,
                ["error"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = errorType,
                    ["reason"] = reason,
                },
            },
        };
    }

    /// <summary>
    /// 缓冲满整批拒绝（HTTP 429）响应体：Filebeat 收到后自动重试.
    /// </summary>
    /// <returns>ES 形状错误体.</returns>
    public static object BufferFull()
    {
        return new
        {
            error = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["root_cause"] = Array.Empty<object>(),
                ["type"] = "es_rejected_execution_exception",
                ["reason"] = "sdata 接入缓冲已满，请稍后重试",
            },
            status = 429,
        };
    }
}
