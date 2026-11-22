using MediatR;
using SData.Infra.Services;

namespace SData.Traces;

/// <summary>
/// trace 概要项.
/// </summary>
public class TraceSummaryItem
{
    /// <summary>trace id.</summary>
    public string TraceId { get; set; } = string.Empty;

    /// <summary>服务名.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>根 span 名称.</summary>
    public string RootName { get; set; } = string.Empty;

    /// <summary>span 数.</summary>
    public long SpanCount { get; set; }

    /// <summary>错误 span 数.</summary>
    public long ErrorCount { get; set; }

    /// <summary>开始时间.</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>耗时（微秒）.</summary>
    public long DurationUs { get; set; }
}

/// <summary>
/// trace 列表查询.
/// </summary>
public class QueryTracesCommand : IRequest<List<TraceSummaryItem>>, IModelValidator<QueryTracesCommand>
{
    /// <summary>开始时间.</summary>
    public DateTimeOffset? Start { get; set; }

    /// <summary>结束时间.</summary>
    public DateTimeOffset? End { get; set; }

    /// <summary>服务名（精确）.</summary>
    public string? Service { get; set; }

    /// <summary>最小耗时（毫秒）.</summary>
    public long? MinDurationMs { get; set; }

    /// <summary>仅含错误.</summary>
    public bool HasError { get; set; }

    /// <summary>返回条数.</summary>
    public int Limit { get; set; } = 50;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryTracesCommand> validate)
    {
        validate.RuleFor(x => x.Limit).InclusiveBetween(1, 200);
    }
}

/// <summary>
/// span 详情项.
/// </summary>
public class SpanItem
{
    /// <summary>trace id.</summary>
    public string TraceId { get; set; } = string.Empty;

    /// <summary>span id.</summary>
    public string SpanId { get; set; } = string.Empty;

    /// <summary>父 span id.</summary>
    public string? ParentSpanId { get; set; }

    /// <summary>开始时间.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>耗时（微秒）.</summary>
    public long DurationUs { get; set; }

    /// <summary>服务名.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>名称.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>类型.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>状态.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>属性（JSON）.</summary>
    public string? Attributes { get; set; }

    /// <summary>事件（JSON）.</summary>
    public string? Events { get; set; }

    /// <summary>资源（JSON）.</summary>
    public string? Resource { get; set; }
}

/// <summary>
/// 完整 trace 查询.
/// </summary>
public class GetTraceCommand : IRequest<List<SpanItem>>, IModelValidator<GetTraceCommand>
{
    /// <summary>trace id.</summary>
    public string TraceId { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<GetTraceCommand> validate)
    {
        validate.RuleFor(x => x.TraceId).NotEmpty().MaximumLength(64).Matches("^[a-f0-9]+$");
    }
}
