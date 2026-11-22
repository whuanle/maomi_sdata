using FluentValidation;
using Mediator;
using SData.Infra.Services;
using SData.Ingest;
using SData.Store.Models;

namespace SData.Monitor;

/// <summary>消费状态和受限时间范围内的历史采样.</summary>
public class QueryMqConsumptionCommand : IRequest<QueryMqConsumptionResponse>, IModelValidator<QueryMqConsumptionCommand>
{
    public DateTimeOffset? From { get; set; }

    public DateTimeOffset? To { get; set; }

    public long? InstanceId { get; set; }

    public static void Validate(AbstractValidator<QueryMqConsumptionCommand> validator)
    {
        validator.RuleFor(x => x).Must(x => x.From.HasValue == x.To.HasValue)
            .WithMessage("开始时间与结束时间必须同时指定");
        validator.RuleFor(x => x).Must(x => !x.From.HasValue || (x.To > x.From && x.To - x.From <= TimeSpan.FromDays(7)))
            .WithMessage("查询时间范围必须大于零且不超过七天");
        validator.RuleFor(x => x.InstanceId).Must(x => !x.HasValue || x > 0);
    }
}

public class QueryMqConsumptionResponse
{
    public DateTimeOffset SampledAt { get; set; } = DateTimeOffset.UtcNow;

    public int WindowSeconds { get; set; } = 60;

    public IngestWriterFailure? WriterFailure { get; set; }

    public IReadOnlyList<MqRuntimeItem> Instances { get; set; } = [];

    public IReadOnlyList<MqNotificationItem> Notifications { get; set; } = [];

    public IReadOnlyList<MqConsumptionRecord> History { get; set; } = [];

    public bool HistoryTruncated { get; set; }
}
