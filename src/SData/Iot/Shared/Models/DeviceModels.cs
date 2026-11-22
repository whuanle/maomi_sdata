using Mediator;
using SData.Infra.Models;
using SData.Infra.Services;

namespace SData.Iot;

/// <summary>
/// 设备项.
/// </summary>
public class DeviceItem
{
    /// <summary>id.</summary>
    public long Id { get; set; }

    /// <summary>设备标识.</summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>名称.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分组.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>标签（JSON）.</summary>
    public string TagsJson { get; set; } = "{}";

    /// <summary>是否启用.</summary>
    public bool Enabled { get; set; }

    /// <summary>创建时间.</summary>
    public DateTimeOffset CreateTime { get; set; }

    /// <summary>更新时间.</summary>
    public DateTimeOffset UpdateTime { get; set; }
}

/// <summary>
/// 保存设备（新增/修改）.
/// </summary>
public class SaveDeviceCommand : IRequest<Simple<long>>, IUserIdContext, IModelValidator<SaveDeviceCommand>
{
    /// <summary>id（0 = 新增）.</summary>
    public long Id { get; set; }

    /// <summary>设备标识.</summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>名称.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分组.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>标签（JSON 对象字符串）.</summary>
    public string TagsJson { get; set; } = "{}";

    /// <summary>是否启用.</summary>
    public bool Enabled { get; set; } = true;

    /// <inheritdoc/>
    public long ContextUserId { get; set; }

    /// <inheritdoc/>
    public string ContextUserRole { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SaveDeviceCommand> validate)
    {
        validate.RuleFor(x => x.DeviceKey).NotEmpty().Matches(DeviceKeyRules.Pattern);
        validate.RuleFor(x => x.Name).NotEmpty().MaximumLength(64);
        validate.RuleFor(x => x.Group).MaximumLength(64);
    }
}

/// <summary>
/// 删除设备.
/// </summary>
public class DeleteDeviceCommand : IRequest<EmptyCommandResponse>, IUserIdContext
{
    /// <summary>设备 id.</summary>
    public long Id { get; set; }

    /// <inheritdoc/>
    public long ContextUserId { get; set; }

    /// <inheritdoc/>
    public string ContextUserRole { get; set; } = string.Empty;
}

/// <summary>
/// 设备列表查询.
/// </summary>
public class QueryDevicesCommand : PagedParameter, IRequest<PagedResult<DeviceItem>>, IModelValidator<QueryDevicesCommand>
{
    /// <summary>关键字（设备标识/名称/分组 模糊）.</summary>
    public string? Search { get; set; }

    /// <summary>分组（精确）.</summary>
    public string? Group { get; set; }

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryDevicesCommand> validate)
    {
        validate.RuleFor(x => x.Page).GreaterThan(0);
        validate.RuleFor(x => x.PageSize).InclusiveBetween(1, 200);
    }
}

/// <summary>
/// 遥测序列数据点.
/// </summary>
public class TelemetryPoint
{
    /// <summary>时间（毫秒时间戳）.</summary>
    public long TimestampMs { get; set; }

    /// <summary>值.</summary>
    public double? Value { get; set; }
}

/// <summary>
/// 遥测序列.
/// </summary>
public class TelemetrySeriesItem
{
    /// <summary>信号名.</summary>
    public string Signal { get; set; } = string.Empty;

    /// <summary>标签（JSON）.</summary>
    public string Labels { get; set; } = "{}";

    /// <summary>数据点.</summary>
    public List<TelemetryPoint> Points { get; set; } = new();
}

/// <summary>
/// 遥测查询响应.
/// </summary>
public class QueryTelemetryCommandResponse
{
    /// <summary>序列列表.</summary>
    public List<TelemetrySeriesItem> Series { get; set; } = new();
}

/// <summary>
/// 遥测区间查询.
/// </summary>
public class QueryTelemetryCommand : IRequest<QueryTelemetryCommandResponse>, IModelValidator<QueryTelemetryCommand>
{
    /// <summary>设备标识.</summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>信号名列表（空 = 全部）.</summary>
    public List<string> Signals { get; set; } = new();

    /// <summary>开始时间.</summary>
    public DateTimeOffset? Start { get; set; }

    /// <summary>结束时间.</summary>
    public DateTimeOffset? End { get; set; }

    /// <summary>聚合方式：avg/sum/max/min/count/last/p95.</summary>
    public string Aggregation { get; set; } = "avg";

    /// <summary>步长（秒）.</summary>
    public int StepSeconds { get; set; } = 60;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryTelemetryCommand> validate)
    {
        validate.RuleFor(x => x.DeviceKey).NotEmpty().MaximumLength(64);
        validate.RuleFor(x => x.StepSeconds).InclusiveBetween(10, 86400);
        validate.RuleFor(x => x.Aggregation).Must(x => new[] { "avg", "sum", "max", "min", "count", "last", "p95" }.Contains(x));
        validate.RuleFor(x => x.Signals).Must(x => x.Count <= 20);
    }
}

/// <summary>
/// 设备最新值.
/// </summary>
public class DeviceLatestValue
{
    /// <summary>信号名.</summary>
    public string Signal { get; set; } = string.Empty;

    /// <summary>数值.</summary>
    public double? NumValue { get; set; }

    /// <summary>字符串值.</summary>
    public string? StrValue { get; set; }

    /// <summary>时间（毫秒时间戳）.</summary>
    public long TimestampMs { get; set; }
}

/// <summary>
/// 设备最新值查询.
/// </summary>
public class QueryDeviceLatestCommand : IRequest<List<DeviceLatestValue>>, IModelValidator<QueryDeviceLatestCommand>
{
    /// <summary>设备标识.</summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryDeviceLatestCommand> validate)
    {
        validate.RuleFor(x => x.DeviceKey).NotEmpty().MaximumLength(64);
    }
}
