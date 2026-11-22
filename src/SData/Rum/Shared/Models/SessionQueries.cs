using Mediator;
using SData.Infra.Models;
using SData.Infra.Services;
using SData.Store;

namespace SData.Rum.Models;

/// <summary>分页会话检索.</summary>
public class SearchRumSessionsCommand : RumSessionFilter, IRequest<RumSessionPage>, IModelValidator<SearchRumSessionsCommand>
{
    /// <summary>项目标识.</summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>起点（含）.</summary>
    public DateTimeOffset From { get; set; }

    /// <summary>终点（不含）.</summary>
    public DateTimeOffset To { get; set; }

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SearchRumSessionsCommand> validator)
    {
        validator.RuleFor(x => x.ProjectKey).NotEmpty();
        validator.RuleFor(x => x.To).GreaterThan(x => x.From);
        validator.RuleFor(x => x.From).GreaterThanOrEqualTo(x => x.To.AddDays(-30));
        validator.RuleFor(x => x.Search).MaximumLength(512);
        validator.RuleFor(x => x.Page).InclusiveBetween(1, 1000000);
        validator.RuleFor(x => x.PageSize).InclusiveBetween(1, 200);
        validator.RuleFor(x => x.Sort).Must(x => new[] { "lastSeen", "firstSeen", "duration", "errors" }.Contains(x));
    }
}

/// <summary>会话事件检索.</summary>
public class QueryRumSessionEventsCommand : IRequest<IReadOnlyList<RumSessionEvent>>, IModelValidator<QueryRumSessionEventsCommand>
{
    /// <summary>项目标识.</summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>会话标识.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>起点（含）.</summary>
    public DateTimeOffset From { get; set; }

    /// <summary>终点（不含）.</summary>
    public DateTimeOffset To { get; set; }

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryRumSessionEventsCommand> validator)
    {
        validator.RuleFor(x => x.ProjectKey).NotEmpty();
        validator.RuleFor(x => x.SessionId).NotEmpty();
        validator.RuleFor(x => x.To).GreaterThan(x => x.From);
        validator.RuleFor(x => x.From).GreaterThanOrEqualTo(x => x.To.AddDays(-30));
    }
}
