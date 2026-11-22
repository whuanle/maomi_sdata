namespace SData.Store;

/// <summary>会话检索条件.</summary>
public class RumSessionFilter
{
    /// <summary>会话、域名、页面或 UA 包含文本.</summary>
    public string? Search { get; set; }

    /// <summary>仅含错误的会话.</summary>
    public bool? HasErrors { get; set; }

    /// <summary>是否包含回放.</summary>
    public bool? HasReplay { get; set; }

    /// <summary>页码.</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数.</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>排序：lastSeen/firstSeen/duration/errors.</summary>
    public string Sort { get; set; } = "lastSeen";
}

/// <summary>会话分页结果.</summary>
public class RumSessionPage
{
    /// <summary>匹配总数.</summary>
    public long Count { get; set; }

    /// <summary>当前页.</summary>
    public IReadOnlyList<RumSessionSummary> Items { get; set; } = [];
}

/// <summary>会话事件.</summary>
public class RumSessionEvent
{
    /// <summary>时间.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>类型.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>名称.</summary>
    public string? Name { get; set; }

    /// <summary>页面.</summary>
    public string? PagePath { get; set; }

    /// <summary>数值.</summary>
    public double? Value { get; set; }

    /// <summary>详情 JSON.</summary>
    public string? Payload { get; set; }
}
