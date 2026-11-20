using MediatR;
using SData.Infra.Services;

namespace SData.Metrics;

/// <summary>
/// 指标序列数据点.
/// </summary>
public class MetricPoint
{
    /// <summary>时间（毫秒时间戳）.</summary>
    public long TimestampMs { get; set; }

    /// <summary>值.</summary>
    public double? Value { get; set; }
}

/// <summary>
/// 指标序列.
/// </summary>
public class MetricSeriesItem
{
    /// <summary>指标名.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>标签（JSON）.</summary>
    public string Labels { get; set; } = "{}";

    /// <summary>数据点.</summary>
    public List<MetricPoint> Points { get; set; } = new();
}

/// <summary>
/// 序列查询响应.
/// </summary>
public class QueryMetricSeriesCommandResponse
{
    /// <summary>序列列表.</summary>
    public List<MetricSeriesItem> Series { get; set; } = new();
}

/// <summary>
/// 指标名列表查询.
/// </summary>
public class QueryMetricNamesCommand : IRequest<List<string>>
{
}

/// <summary>
/// 指标序列区间查询.
/// </summary>
public class QueryMetricSeriesCommand : IRequest<QueryMetricSeriesCommandResponse>, IModelValidator<QueryMetricSeriesCommand>
{
    /// <summary>指标名.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>标签过滤（精确匹配）.</summary>
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>开始时间.</summary>
    public DateTimeOffset? Start { get; set; }

    /// <summary>结束时间.</summary>
    public DateTimeOffset? End { get; set; }

    /// <summary>聚合方式：avg/sum/max/min/count/last/p95.</summary>
    public string Aggregation { get; set; } = "avg";

    /// <summary>降采样步长（秒）.</summary>
    public int StepSeconds { get; set; } = 60;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryMetricSeriesCommand> validate)
    {
        validate.RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        validate.RuleFor(x => x.StepSeconds).InclusiveBetween(10, 86400);
        validate.RuleFor(x => x.Aggregation).Must(x => new[] { "avg", "sum", "max", "min", "count", "last", "p95" }.Contains(x));
    }
}
