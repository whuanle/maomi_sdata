using MediatR;
using SData.Infra.Models;
using SData.Infra.Services;

namespace SData.Dashboard;

/// <summary>
/// 仪表盘项.
/// </summary>
public class DashboardItem
{
    /// <summary>id.</summary>
    public long Id { get; set; }

    /// <summary>名称.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>类型：view（仪表盘）.</summary>
    public string Kind { get; set; } = "view";

    /// <summary>描述.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>配置（JSON）.</summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>创建时间.</summary>
    public DateTimeOffset CreateTime { get; set; }

    /// <summary>更新时间.</summary>
    public DateTimeOffset UpdateTime { get; set; }
}

/// <summary>
/// 保存仪表盘.
/// </summary>
public class SaveDashboardCommand : IRequest<Simple<long>>, IUserIdContext, IModelValidator<SaveDashboardCommand>
{
    /// <summary>id（0 = 新增）.</summary>
    public long Id { get; set; }

    /// <summary>名称.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>类型：view（仪表盘）.</summary>
    public string Kind { get; set; } = "view";

    /// <summary>描述.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>配置（JSON）.</summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>可选乐观并发校验原配置.</summary>
    public string? ExpectedConfigJson { get; set; }

    /// <inheritdoc/>
    public long ContextUserId { get; set; }

    /// <inheritdoc/>
    public string ContextUserRole { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SaveDashboardCommand> validate)
    {
        validate.RuleFor(x => x.Name).NotEmpty().MaximumLength(64);
        validate.RuleFor(x => x.Kind).Equal("view").WithMessage("仅支持普通仪表盘");
        validate.RuleFor(x => x.Description).MaximumLength(256);
        validate.RuleFor(x => x.ConfigJson).MaximumLength(1_000_000);
    }
}

/// <summary>
/// 仪表盘列表.
/// </summary>
public class QueryDashboardsCommand : PagedParameter, IRequest<PagedResult<DashboardItem>>, IModelValidator<QueryDashboardsCommand>
{
    /// <summary>类型过滤（仅支持 view）.</summary>
    public string? Kind { get; set; }

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryDashboardsCommand> validate)
    {
        validate.RuleFor(x => x.Kind).Must(x => x == null || x == "view").WithMessage("仅支持普通仪表盘");
        validate.RuleFor(x => x.Page).GreaterThan(0);
        validate.RuleFor(x => x.PageSize).InclusiveBetween(1, 200);
    }
}

/// <summary>
/// 仪表盘详情.
/// </summary>
public class GetDashboardCommand : IRequest<DashboardItem>, IModelValidator<GetDashboardCommand>
{
    /// <summary>id.</summary>
    public long Id { get; set; }

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<GetDashboardCommand> validate)
    {
        validate.RuleFor(x => x.Id).GreaterThan(0);
    }
}

/// <summary>
/// 删除仪表盘.
/// </summary>
public class DeleteDashboardCommand : IRequest<EmptyCommandResponse>, IUserIdContext
{
    /// <summary>id.</summary>
    public long Id { get; set; }

    /// <inheritdoc/>
    public long ContextUserId { get; set; }

    /// <inheritdoc/>
    public string ContextUserRole { get; set; } = string.Empty;
}

/// <summary>
/// 面板数据（两种形态二选一：Series 时间序列 / Columns+Rows 表格；旧 sql 面板恒为表格）.
/// </summary>
public class PanelData
{
    /// <summary>自定义查询节点诊断.</summary>
    public List<CustomPanelNodeData> Nodes { get; set; } = new();
    /// <summary>面板 id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>时间序列（非空 = 时间序列面板）.</summary>
    public List<PanelSeries> Series { get; set; } = new();

    /// <summary>列名（表格形态）.</summary>
    public List<string> Columns { get; set; } = new();

    /// <summary>数据行（表格形态）.</summary>
    public List<List<object?>> Rows { get; set; } = new();

    /// <summary>耗时（毫秒）.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>查询结果是否被行数上限截断.</summary>
    public bool Truncated { get; set; }

    /// <summary>本次查询的实际起止时间.</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>本次查询的实际结束时间（不含）.</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>错误信息.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// 仪表盘数据响应.
/// </summary>
public class GetDashboardDataCommandResponse
{
    /// <summary>各面板数据.</summary>
    public List<PanelData> Panels { get; set; } = new();
}

/// <summary>
/// 拉取仪表盘全部面板数据（执行面板配置的查询）.
/// </summary>
public class GetDashboardDataCommand : IRequest<GetDashboardDataCommandResponse>, IModelValidator<GetDashboardDataCommand>
{
    /// <summary>id.</summary>
    public long Id { get; set; }

    /// <summary>起始时间覆盖（缺省 = 仪表盘时间配置）.</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>结束时间覆盖（缺省 = 当前时间）.</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>行数上限（每面板）.</summary>
    public int RowLimit { get; set; } = 1000;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<GetDashboardDataCommand> validate)
    {
        validate.RuleFor(x => x.Id).GreaterThan(0);
        validate.RuleFor(x => x.RowLimit).InclusiveBetween(1, 10_000);
    }
}
