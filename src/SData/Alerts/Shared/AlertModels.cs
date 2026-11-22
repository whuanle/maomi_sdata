using FluentValidation;
using Mediator;
using SData.Infra.Models;
using SData.Infra.Services;

namespace SData.Alerts;

public class SaveAlertCommand : IRequest<AlertItem>, IUserIdContext, IModelValidator<SaveAlertCommand>
{
    public string? Id { get; set; }
    public long Version { get; set; }
    public string? SavedQueryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Domain { get; set; } = "logs";
    public string Definition { get; set; } = "{}";
    public string Condition { get; set; } = string.Empty;
    public Dictionary<string, object?> Parameters { get; set; } = new();
    public List<string> Channels { get; set; } = new();
    public string Note { get; set; } = string.Empty;
    public string Operator { get; set; } = "gte";
    public long Threshold { get; set; } = 1;
    public int WindowMinutes { get; set; } = 5;
    public int DelayMinutes { get; set; } = 1;
    public int ConsecutiveWindows { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public long ContextUserId { get; set; }
    public string ContextUserRole { get; set; } = string.Empty;

    public static void Validate(AbstractValidator<SaveAlertCommand> validate)
    {
        validate.RuleFor(x => x.Id).Must(x => x == null || long.TryParse(x, out var id) && id > 0);
        validate.RuleFor(x => x.SavedQueryId).Must(x => x == null || long.TryParse(x, out var id) && id > 0);
        validate.RuleFor(x => x.Version).GreaterThanOrEqualTo(0);
        validate.RuleFor(x => x.Name).NotEmpty().MaximumLength(64);
        validate.RuleFor(x => x.Domain).Must(x => x is "logs" or "traces");
        validate.RuleFor(x => x.Definition).NotEmpty().MaximumLength(16384);
        validate.RuleFor(x => x.Condition).NotNull().MaximumLength(8192);
        validate.RuleFor(x => x.Parameters).NotNull().Must(x => x != null && x.Count <= 128);
        validate.RuleFor(x => x.Channels).NotNull().Must(x => x != null && x.Count is > 0 and <= 8 && x.Distinct().Count() == x.Count && x.All(id => long.TryParse(id, out var value) && value > 0));
        validate.RuleFor(x => x.Note).MaximumLength(2048);
        validate.RuleFor(x => x.Operator).Must(x => x is "gte" or "gt" or "lte" or "lt");
        validate.RuleFor(x => x.Threshold).InclusiveBetween(0, 1_000_000);
        validate.RuleFor(x => x.WindowMinutes).InclusiveBetween(1, 60);
        validate.RuleFor(x => x.DelayMinutes).InclusiveBetween(0, 10);
        validate.RuleFor(x => x.ConsecutiveWindows).InclusiveBetween(1, 10);
    }
}

public class AlertItem : SaveAlertCommand
{
    public string Status { get; set; } = "pending";
    public string Error { get; set; } = string.Empty;
    public DateTimeOffset CreateTime { get; set; }
    public DateTimeOffset UpdateTime { get; set; }
    public string CreateUserId { get; set; } = string.Empty;
    public string UpdateUserId { get; set; } = string.Empty;
}

public class QueryAlertsCommand : IRequest<List<AlertItem>>, IModelValidator<QueryAlertsCommand>
{
    public string? SavedQueryId { get; set; }
    public static void Validate(AbstractValidator<QueryAlertsCommand> validate)
        => validate.RuleFor(x => x.SavedQueryId).Must(x => x == null || long.TryParse(x, out var id) && id > 0);
}

public class DeleteAlertCommand : IRequest<bool>, IUserIdContext, IModelValidator<DeleteAlertCommand>
{
    public string Id { get; set; } = string.Empty;
    public long Version { get; set; }
    public long ContextUserId { get; set; }
    public string ContextUserRole { get; set; } = string.Empty;
    public static void Validate(AbstractValidator<DeleteAlertCommand> validate)
    {
        validate.RuleFor(x => x.Id).Must(x => long.TryParse(x, out var id) && id > 0);
        validate.RuleFor(x => x.Version).GreaterThan(0);
    }
}

public class QueryAlertHistoryCommand : IRequest<AlertHistory>, IModelValidator<QueryAlertHistoryCommand>
{
    public string Id { get; set; } = string.Empty;
    public static void Validate(AbstractValidator<QueryAlertHistoryCommand> validate)
        => validate.RuleFor(x => x.Id).Must(x => long.TryParse(x, out var id) && id > 0);
}

public class PreviewAlertCommand : IRequest<AlertPoint>, IModelValidator<PreviewAlertCommand>
{
    public string Domain { get; set; } = "logs";
    public string Condition { get; set; } = string.Empty;
    public Dictionary<string, object?> Parameters { get; set; } = new();
    public int WindowMinutes { get; set; } = 5;
    public static void Validate(AbstractValidator<PreviewAlertCommand> validate)
    {
        validate.RuleFor(x => x.Domain).Must(x => x is "logs" or "traces");
        validate.RuleFor(x => x.Condition).NotNull().MaximumLength(8192);
        validate.RuleFor(x => x.Parameters).NotNull().Must(x => x != null && x.Count <= 128);
        validate.RuleFor(x => x.WindowMinutes).InclusiveBetween(1, 60);
    }
}

public record AlertPoint(DateTimeOffset Time, long Count, bool LowerBound, string Status, string Error);
public record AlertDeliveryItem(string ChannelId, string Status, int Attempts, string Result);
public record AlertHistory(List<AlertPoint> Points, List<AlertDeliveryItem> Deliveries);
public record AlertChannelItem(string Id, string Name, string Type, bool Enabled);
public class QueryAlertChannelsCommand : IRequest<List<AlertChannelItem>>, IModelValidator<QueryAlertChannelsCommand>
{
    public static void Validate(AbstractValidator<QueryAlertChannelsCommand> validate) { }
}
