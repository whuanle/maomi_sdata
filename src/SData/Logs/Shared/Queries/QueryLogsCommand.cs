using MediatR;
using SData.Infra.Models;
using SData.Infra.Services;

namespace SData.Logs;

/// <summary>
/// 日志查询.
/// </summary>
public class QueryLogsCommand : PagedParameter, IRequest<PagedResult<LogItem>>, IModelValidator<QueryLogsCommand>
{
    /// <summary>
    /// 开始时间（缺省 = 1 小时前）.
    /// </summary>
    public DateTimeOffset? Start { get; set; }

    /// <summary>
    /// 结束时间（缺省 = 当前）.
    /// </summary>
    public DateTimeOffset? End { get; set; }

    /// <summary>
    /// 服务名（精确）.
    /// </summary>
    public string? Service { get; set; }

    /// <summary>
    /// 级别（逗号分隔：INFO,WARN,ERROR）.
    /// </summary>
    public string? Severity { get; set; }

    /// <summary>
    /// 正文关键字（包含匹配）.
    /// </summary>
    public string? Search { get; set; }

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryLogsCommand> validate)
    {
        validate.RuleFor(x => x.Page).GreaterThan(0);
        validate.RuleFor(x => x.PageSize).InclusiveBetween(1, 200);
        validate.RuleFor(x => x.Search).MaximumLength(200);
        validate.RuleFor(x => x.Start).LessThan(x => x.End ?? DateTimeOffset.MaxValue).When(x => x.Start.HasValue && x.End.HasValue);
    }
}

/// <summary>
/// 日志项.
/// </summary>
public class LogItem
{
    /// <summary>时间.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>服务名.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>级别.</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>正文.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>trace id.</summary>
    public string? TraceId { get; set; }

    /// <summary>span id.</summary>
    public string? SpanId { get; set; }

    /// <summary>属性（JSON）.</summary>
    public string? Attributes { get; set; }

    /// <summary>资源（JSON）.</summary>
    public string? Resource { get; set; }
}

/// <summary>
/// 服务名列表查询.
/// </summary>
public class QueryLogServicesCommand : IRequest<List<string>>
{
}
