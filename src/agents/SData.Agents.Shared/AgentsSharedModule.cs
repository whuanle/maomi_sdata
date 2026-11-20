using Maomi;
using FluentValidation;

namespace SData.Agents;

/// <summary>Agent 协议共享契约.</summary>
public class AgentsSharedModule : IModule
{
    public void ConfigureServices(ServiceContext context) { }
}

/// <summary>只接受固定工具和结构化参数，不接受 SQL.</summary>
public interface IAgentQueries
{
    Task<object> ExecuteAsync(string tool, AgentQuery query, CancellationToken cancellationToken);
}

public class AgentQuery : AgentSeriesQuery, MediatR.IModelValidator<AgentQuery>
{
    public static new void Validate(FluentValidation.AbstractValidator<AgentQuery> validate)
    {
        AgentValidation.Window(validate);
        validate.RuleFor(x => x.Labels).NotNull().Must(x => x.Count <= 8 && x.All(p => p.Key.Length <= 64 && p.Value != null && p.Value.Length <= 512));
        validate.RuleFor(x => x.Aggregation).NotEmpty().MaximumLength(16);
        validate.RuleFor(x => x.Limit).InclusiveBetween(1, 200);
        validate.RuleFor(x => x.StepSeconds).InclusiveBetween(10, 86400);
        validate.RuleFor(x => x).Must(x => new[] { x.Service, x.Severity, x.Keyword, x.TraceId, x.Status, x.Metric }.All(v => v == null || v.Length <= 512));
        validate.RuleFor(x => x).Must(x => !x.MinDurationMs.HasValue || (double.IsFinite(x.MinDurationMs.Value) && x.MinDurationMs.Value >= 0));
        validate.RuleFor(x => x).Must(x => { var end = x.To ?? DateTimeOffset.UtcNow; var start = x.From ?? end.AddMinutes(-15); return start < end && end - start <= TimeSpan.FromHours(24); });
    }

    public string? Service { get; set; }
    public string? Severity { get; set; }
    public string? Keyword { get; set; }
    public string? TraceId { get; set; }
    public string? Status { get; set; }
    public double? MinDurationMs { get; set; }
}
