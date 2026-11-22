namespace SData.Store;

/// <summary>
/// RUM 聚合查询结果模型（T613，查询面消费）.
/// </summary>
public class RumPerformanceAggregates
{
    /// <summary>页面浏览量（rum_session 表 pageview 行数）.</summary>
    public long PageViews { get; set; }

    /// <summary>独立访客（distinct session_id，近似口径）.</summary>
    public long UniqueVisitors { get; set; }

    /// <summary>Web Vitals p75（vital 事件按 event_name 聚合：LCP/INP/CLS）.</summary>
    public Dictionary<string, double> VitalP75 { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// JS 错误聚合项.
/// </summary>
public class RumErrorAggregate
{
    /// <summary>错误名（message/name）.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>发生次数.</summary>
    public long Count { get; set; }
}

/// <summary>
/// 会话摘要项.
/// </summary>
public class RumSessionSummary
{
    /// <summary>会话标识.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>查询窗口内首次活动时间.</summary>
    public DateTimeOffset FirstSeen { get; set; }

    /// <summary>窗口内页面浏览数.</summary>
    public long PageViews { get; set; }

    /// <summary>窗口内错误数.</summary>
    public long ErrorCount { get; set; }

    /// <summary>窗口内事件数.</summary>
    public long EventCount { get; set; }

    /// <summary>窗口内是否有回放分片.</summary>
    public bool HasReplay { get; set; }

    /// <summary>最近活动页面.</summary>
    public string? PagePath { get; set; }

    /// <summary>最近活跃时间.</summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>用户代理.</summary>
    public string? UserAgent { get; set; }

    /// <summary>站点域名.</summary>
    public string? Domain { get; set; }
}

/// <summary>
/// 热力栅格单元（T615；坐标为 0..gridCells-1 的归一化网格索引）.
/// </summary>
public class RumHeatmapCell
{
    /// <summary>网格 X（0..GridCells-1）.</summary>
    public int X { get; set; }

    /// <summary>网格 Y（0..GridCells-1）.</summary>
    public int Y { get; set; }

    /// <summary>点击数.</summary>
    public long Count { get; set; }
}

/// <summary>
/// 点击目标聚合项（T616；selector 优先，回退 targetId）.
/// </summary>
public class RumClickTarget
{
    /// <summary>目标摘要.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>点击数.</summary>
    public long Count { get; set; }
}

/// <summary>
/// 热力聚合结果（栅格 + 总数 + 目标 TopN，T616）.
/// </summary>
public class RumHeatmapAggregates
{
    /// <summary>栅格单元（count 降序）.</summary>
    public IReadOnlyList<RumHeatmapCell> Cells { get; set; } = [];

    /// <summary>过滤范围内点击总数.</summary>
    public long TotalClicks { get; set; }

    /// <summary>点击目标 Top N.</summary>
    public IReadOnlyList<RumClickTarget> TopTargets { get; set; } = [];
}

/// <summary>
/// 页面树节点（T615；按页面路径聚合交互与错误）.
/// </summary>
public class RumPageNode
{
    /// <summary>页面路径.</summary>
    public string PagePath { get; set; } = string.Empty;

    /// <summary>点击数.</summary>
    public long Clicks { get; set; }

    /// <summary>错误数.</summary>
    public long Errors { get; set; }
}

/// <summary>
/// RUM 查询接口：基于 rum_* 天表聚合（实现 Store.Core；跨域经接口消费）.
/// </summary>
public interface IRumStore
{
    /// <summary>
    /// 性能汇总：PV/UV + Web Vitals p75.
    /// </summary>
    /// <param name="projectKey">项目标识.</param>
    /// <param name="from">开始（含）.</param>
    /// <param name="to">结束（不含）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>汇总.</returns>
    Task<RumPerformanceAggregates> AggregatePerformanceAsync(string projectKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>
    /// JS 错误 Top N.
    /// </summary>
    /// <param name="projectKey">项目标识.</param>
    /// <param name="from">开始（含）.</param>
    /// <param name="to">结束（不含）.</param>
    /// <param name="topN">前 N 项.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>错误聚合（按次数降序）.</returns>
    Task<IReadOnlyList<RumErrorAggregate>> AggregateErrorsAsync(string projectKey, DateTimeOffset from, DateTimeOffset to, int topN, CancellationToken cancellationToken = default);

    /// <summary>
    /// 会话列表（按最近活跃降序）.
    /// </summary>
    /// <param name="projectKey">项目标识.</param>
    /// <param name="from">开始（含）.</param>
    /// <param name="to">结束（不含）.</param>
    /// <param name="limit">返回上限.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>会话摘要.</returns>
    Task<IReadOnlyList<RumSessionSummary>> ListSessionsAsync(string projectKey, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default);
    /// <summary>
    /// 点击热力聚合（栅格分桶 + 总数 + 目标 TopN；坐标文档绝对坐标优先，回退视口坐标，T616）.
    /// </summary>
    /// <param name="projectKey">项目标识.</param>
    /// <param name="from">开始（含）.</param>
    /// <param name="to">结束（不含）.</param>
    /// <param name="gridCells">网格边长（N x N）.</param>
    /// <param name="pagePath">页面路径过滤（null/空 = 全部页面）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>热力聚合.</returns>
    Task<RumHeatmapAggregates> AggregateHeatmapAsync(string projectKey, DateTimeOffset from, DateTimeOffset to, int gridCells, string? pagePath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 页面树聚合（按 page_path 统计 click/error 事件数，click 降序）.
    /// </summary>
    /// <param name="projectKey">项目标识.</param>
    /// <param name="from">开始（含）.</param>
    /// <param name="to">结束（不含）.</param>
    /// <param name="limit">返回上限.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>页面节点.</returns>
    Task<IReadOnlyList<RumPageNode>> AggregatePageTreeAsync(string projectKey, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// 会话回放分段（按 seq 升序；rrweb 事件 gz+base64 分段，T614）.
    /// </summary>
    /// <param name="sessionId">会话标识.</param>
    /// <param name="from">开始（含）.</param>
    /// <param name="to">结束（不含）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>分段列表.</returns>
    Task<IReadOnlyList<RumReplayChunk>> GetReplayAsync(string projectKey, string sessionId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>分页检索会话（筛选在聚合后执行）.</summary>
    Task<RumSessionPage> SearchSessionsAsync(string projectKey, DateTimeOffset from, DateTimeOffset to, RumSessionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>按项目与会话查询窗口内事件.</summary>
    Task<IReadOnlyList<RumSessionEvent>> ListSessionEventsAsync(string projectKey, string sessionId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}

/// <summary>
/// 会话回放分段项（T614）.
/// </summary>
public class RumReplayChunk
{
    /// <summary>分段序号.</summary>
    public long Seq { get; set; }

    /// <summary>分段时间.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>分段内容（gzip(rrweb 事件 JSON) 的 base64）.</summary>
    public string BlobBase64 { get; set; } = string.Empty;
}
