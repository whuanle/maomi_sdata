using MediatR;
using SData.Infra.Models;
using SData.Infra.Services;
using SData.Store;

namespace SData.Rum.Models;

/// <summary>
/// RUM 性能汇总查询（PV/UV + Web Vitals p75）.
/// </summary>
public class QueryRumPerformanceCommand : IRequest<RumPerformanceResponse>, IModelValidator<QueryRumPerformanceCommand>
{
    /// <summary>项目标识.</summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>开始时间（含）.</summary>
    public DateTimeOffset From { get; set; }

    /// <summary>结束时间（不含）.</summary>
    public DateTimeOffset To { get; set; }

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryRumPerformanceCommand> validator)
    {
        validator.RuleFor(x => x.ProjectKey).NotEmpty().WithMessage("projectKey 不能为空");
        validator.RuleFor(x => x.To).GreaterThan(x => x.From).WithMessage("to 必须晚于 from");
        validator.RuleFor(x => x.From).GreaterThanOrEqualTo(x => x.To.AddDays(-MaxQueryDays)).WithMessage($"查询跨度不能超过 {MaxQueryDays} 天");
    }

    /// <summary>最大查询跨度（天）.</summary>
    public const int MaxQueryDays = 30;
}

/// <summary>
/// RUM 性能汇总响应.
/// </summary>
public class RumPerformanceResponse
{
    /// <summary>页面浏览量.</summary>
    public long PageViews { get; set; }

    /// <summary>独立访客（distinct 会话近似）.</summary>
    public long UniqueVisitors { get; set; }

    /// <summary>Web Vitals p75（LCP/INP/CLS）.</summary>
    public Dictionary<string, double> VitalP75 { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// RUM JS 错误 Top 查询.
/// </summary>
public class QueryRumErrorsCommand : IRequest<RumErrorsResponse>, IModelValidator<QueryRumErrorsCommand>
{
    /// <summary>项目标识.</summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>开始时间（含）.</summary>
    public DateTimeOffset From { get; set; }

    /// <summary>结束时间（不含）.</summary>
    public DateTimeOffset To { get; set; }

    /// <summary>前 N 项（缺省 10）.</summary>
    public int TopN { get; set; } = 10;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryRumErrorsCommand> validator)
    {
        validator.RuleFor(x => x.ProjectKey).NotEmpty().WithMessage("projectKey 不能为空");
        validator.RuleFor(x => x.To).GreaterThan(x => x.From).WithMessage("to 必须晚于 from");
        validator.RuleFor(x => x.From).GreaterThanOrEqualTo(x => x.To.AddDays(-30)).WithMessage("查询跨度不能超过 30 天");
        validator.RuleFor(x => x.TopN).InclusiveBetween(1, 100).WithMessage("topN 取值范围 1–100");
    }
}

/// <summary>
/// RUM JS 错误 Top 响应.
/// </summary>
public class RumErrorsResponse
{
    /// <summary>错误聚合项（按次数降序）.</summary>
    public List<RumErrorAggregate> Items { get; set; } = [];
}

/// <summary>
/// RUM 会话列表查询.
/// </summary>
public class QueryRumSessionsCommand : IRequest<RumSessionsResponse>, IModelValidator<QueryRumSessionsCommand>
{
    /// <summary>项目标识.</summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>开始时间（含）.</summary>
    public DateTimeOffset From { get; set; }

    /// <summary>结束时间（不含）.</summary>
    public DateTimeOffset To { get; set; }

    /// <summary>返回上限（缺省 50）.</summary>
    public int Limit { get; set; } = 50;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryRumSessionsCommand> validator)
    {
        validator.RuleFor(x => x.ProjectKey).NotEmpty().WithMessage("projectKey 不能为空");
        validator.RuleFor(x => x.To).GreaterThan(x => x.From).WithMessage("to 必须晚于 from");
        validator.RuleFor(x => x.From).GreaterThanOrEqualTo(x => x.To.AddDays(-30)).WithMessage("查询跨度不能超过 30 天");
        validator.RuleFor(x => x.Limit).InclusiveBetween(1, 200).WithMessage("limit 取值范围 1–200");
    }
}

/// <summary>
/// RUM 会话列表响应.
/// </summary>
public class RumSessionsResponse
{
    /// <summary>会话摘要（按最近活跃降序）.</summary>
    public List<RumSessionSummary> Items { get; set; } = [];
}

/// <summary>
/// RUM 会话回放查询（C3：GET /rum/v1/replay/{session}，T614）.
/// </summary>
public class QueryRumReplayCommand : IRequest<RumReplayResponse>, IModelValidator<QueryRumReplayCommand>
{
    /// <summary>项目标识.</summary>
    public string ProjectKey { get; set; } = string.Empty;
    /// <summary>会话标识.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>开始时间（含）.</summary>
    public DateTimeOffset From { get; set; }

    /// <summary>结束时间（不含）.</summary>
    public DateTimeOffset To { get; set; }

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryRumReplayCommand> validator)
    {
        validator.RuleFor(x => x.ProjectKey).NotEmpty().WithMessage("projectKey 不能为空");
        validator.RuleFor(x => x.SessionId).NotEmpty().WithMessage("sessionId 不能为空");
        validator.RuleFor(x => x.To).GreaterThan(x => x.From).WithMessage("to 必须晚于 from");
        validator.RuleFor(x => x.From).GreaterThanOrEqualTo(x => x.To.AddDays(-30)).WithMessage("查询跨度不能超过 30 天");
    }
}

/// <summary>
/// RUM 会话回放响应（分段按 seq 升序）.
/// </summary>
public class RumReplayResponse
{
    /// <summary>会话标识.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>回放分段（gzip(rrweb 事件 JSON) 的 base64，按 seq 升序）.</summary>
    public List<RumReplayChunk> Chunks { get; set; } = [];
}

/// <summary>
/// RUM 点击热力查询（T615；T616 增加页面过滤与文档坐标）.
/// </summary>
public class QueryRumHeatmapCommand : IRequest<RumHeatmapResponse>, IModelValidator<QueryRumHeatmapCommand>
{
    /// <summary>项目标识.</summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>开始时间（含）.</summary>
    public DateTimeOffset From { get; set; }

    /// <summary>结束时间（不含）.</summary>
    public DateTimeOffset To { get; set; }

    /// <summary>网格边长（N x N，缺省 20，范围 2–100）.</summary>
    public int GridCells { get; set; } = 20;

    /// <summary>页面路径过滤（空 = 全部页面）.</summary>
    public string? PagePath { get; set; }

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryRumHeatmapCommand> validator)
    {
        validator.RuleFor(x => x.ProjectKey).NotEmpty().WithMessage("projectKey 不能为空");
        validator.RuleFor(x => x.To).GreaterThan(x => x.From).WithMessage("to 必须晚于 from");
        validator.RuleFor(x => x.From).GreaterThanOrEqualTo(x => x.To.AddDays(-30));
        validator.RuleFor(x => x.GridCells).InclusiveBetween(2, 100).WithMessage("gridCells 取值范围 2–100");
        validator.RuleFor(x => x.PagePath).MaximumLength(512).WithMessage("pagePath 长度不能超过 512");
    }
}

/// <summary>
/// RUM 点击热力响应.
/// </summary>
public class RumHeatmapResponse
{
    /// <summary>网格单元.</summary>
    public List<SData.Store.RumHeatmapCell> Cells { get; set; } = [];

    /// <summary>过滤范围内点击总数.</summary>
    public long TotalClicks { get; set; }

    /// <summary>点击目标 Top N（selector 优先，回退 targetId）.</summary>
    public List<SData.Store.RumClickTarget> TopTargets { get; set; } = [];
}

/// <summary>
/// RUM 站点地图（页面树）查询.
/// </summary>
public class QueryRumSitemapCommand : IRequest<RumSitemapResponse>, IModelValidator<QueryRumSitemapCommand>
{
    /// <summary>项目标识.</summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>开始时间（含）.</summary>
    public DateTimeOffset From { get; set; }

    /// <summary>结束时间（不含）.</summary>
    public DateTimeOffset To { get; set; }

    /// <summary>返回上限（缺省 50）.</summary>
    public int Limit { get; set; } = 50;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryRumSitemapCommand> validator)
    {
        validator.RuleFor(x => x.ProjectKey).NotEmpty().WithMessage("projectKey 不能为空");
        validator.RuleFor(x => x.To).GreaterThan(x => x.From).WithMessage("to 必须晚于 from");
        validator.RuleFor(x => x.From).GreaterThanOrEqualTo(x => x.To.AddDays(-30));
        validator.RuleFor(x => x.Limit).InclusiveBetween(1, 200).WithMessage("limit 取值范围 1–200");
    }
}

/// <summary>
/// RUM 站点地图响应.
/// </summary>
public class RumSitemapResponse
{
    /// <summary>页面节点（click 降序）.</summary>
    public List<SData.Store.RumPageNode> Items { get; set; } = [];
}
