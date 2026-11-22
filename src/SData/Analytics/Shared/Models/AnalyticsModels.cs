using System.Text.Json;
using System.Text.RegularExpressions;
using MediatR;
using SData.Infra.Models;
using SData.Infra.Services;
using SData.Store;

namespace SData.Analytics;

/// <summary>查询当前 DuckDB 表目录.</summary>
public class QueryAnalyticsTablesCommand : IRequest<IReadOnlyList<AnalyticsTableItem>>, IModelValidator<QueryAnalyticsTablesCommand>
{
    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryAnalyticsTablesCommand> validate)
    {
    }
}


/// <summary>
/// 分析查询结果.
/// </summary>
public class AnalyticsQueryResponse
{
    /// <summary>列名.</summary>
    public List<string> Columns { get; set; } = new();

    /// <summary>数据行.</summary>
    public List<List<object?>> Rows { get; set; } = new();

    /// <summary>耗时（毫秒）.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>是否被截断.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// 执行只读分析查询（DuckDB，仅 SELECT/WITH）.
/// </summary>
public class ExecuteQueryCommand : IRequest<AnalyticsQueryResponse>, IModelValidator<ExecuteQueryCommand>
{
    /// <summary>SQL（仅单条 SELECT/WITH）.</summary>
    public string Sql { get; set; } = string.Empty;

    /// <summary>行数上限.</summary>
    public int RowLimit { get; set; } = 1000;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<ExecuteQueryCommand> validate)
    {
        validate.RuleFor(x => x.Sql).NotEmpty().MaximumLength(200_000);
        validate.RuleFor(x => x.RowLimit).InclusiveBetween(1, 10_000);
    }
}

/// <summary>
/// 统一搜索 SQL 直查（DuckDB：{{logs}} 等表占位符 + $start/$end 及其余 $name 命名参数绑定）.
/// </summary>
public partial class ExecuteSearchSqlCommand : IRequest<AnalyticsQueryResponse>, IModelValidator<ExecuteSearchSqlCommand>
{
    /// <summary>SQL（仅单条 SELECT/WITH；{{logs}}/{{traces}}/{{metrics}}/{{iot}} 展开为区间内按天表 UNION）.</summary>
    public string Sql { get; set; } = string.Empty;

    /// <summary>起始时间（含，绑定 $start）.</summary>
    public DateTimeOffset From { get; set; }

    /// <summary>结束时间（不含，绑定 $end）.</summary>
    public DateTimeOffset To { get; set; }

    /// <summary>行数上限.</summary>
    public int RowLimit { get; set; } = 500;

    /// <summary>
    /// 命名参数字典（SQL 中 $name 引用的绑定值，键不含 $ 前缀；$start/$end 由 From/To 提供，无需传入）.
    /// 值仅允许字符串/数字/布尔/null，全部以 DuckDBParameter 绑定，禁止拼接进 SQL 文本.
    /// </summary>
    public Dictionary<string, object?>? Parameters { get; set; }

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<ExecuteSearchSqlCommand> validate)
    {
        validate.RuleFor(x => x.Sql).NotEmpty().MaximumLength(200_000);
        validate.RuleFor(x => x.From).NotEqual(default(DateTimeOffset));
        validate.RuleFor(x => x.To).NotEqual(default(DateTimeOffset));
        validate.RuleFor(x => x.RowLimit).InclusiveBetween(1, 10_000);
        validate.RuleFor(x => x.Parameters).Must(parameters => parameters == null || parameters.Count <= 64)
            .WithMessage("命名参数数量不得超过 64");
        validate.RuleFor(x => x.Parameters)
            .Must(parameters => parameters == null || parameters.Keys.All(key => ParamNameRegex().IsMatch(key)))
            .WithMessage("命名参数名仅允许小写字母开头，含小写字母/数字/下划线");
        validate.RuleFor(x => x.Parameters).Must(parameters => parameters == null || parameters.Values.All(IsAllowedValue))
            .WithMessage("命名参数值仅允许字符串/数字/布尔/null");
    }

    /// <summary>参数名合法性（与 SearchSqlExpander 的 $name 扫描规则一致：字母/下划线开头 + 字母数字下划线）.</summary>
    [GeneratedRegex(@"^[a-z][a-z0-9_]*$")]
    private static partial Regex ParamNameRegex();

    /// <summary>参数值白名单：null / 基元类型 / JSON 反序列化的 String、Number、布尔、Null.</summary>
    private static bool IsAllowedValue(object? value)
    {
        if (value == null)
        {
            return true;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind is JsonValueKind.String or JsonValueKind.Number
                or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
        }

        return value is string or bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }
}

/// <summary>
/// 保存的查询项.
/// </summary>
public class SavedQueryItem
{
    /// <summary>无损字符串 id，供浏览器关联告警与路由使用.</summary>
    public string ExactId => Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>id.</summary>
    public long Id { get; set; }

    /// <summary>名称.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>类型（sql = 分析 SQL；search = 统一搜索保存）.</summary>
    public string Kind { get; set; } = "sql";

    /// <summary>SQL（kind=search 时为空）.</summary>
    public string SqlText { get; set; } = string.Empty;

    /// <summary>搜索状态 JSON（kind=search 时有效：domain/query/rangeMs）.</summary>
    public string Definition { get; set; } = string.Empty;

    /// <summary>描述.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>创建时间.</summary>
    public DateTimeOffset CreateTime { get; set; }

    /// <summary>更新时间.</summary>
    public DateTimeOffset UpdateTime { get; set; }

    /// <summary>创建人 id.</summary>
    public long CreateUserId { get; set; }

    /// <summary>更新人 id.</summary>
    public long UpdateUserId { get; set; }
}

/// <summary>
/// 保存查询.
/// </summary>
public class SaveQueryCommand : IRequest<Simple<long>>, IUserIdContext, IModelValidator<SaveQueryCommand>
{
    /// <summary>id（0 = 新增），兼容浏览器无损字符串.</summary>
    [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)]
    public long Id { get; set; }

    /// <summary>名称.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>类型（sql = 分析 SQL，默认；search = 统一搜索保存）.</summary>
    public string Kind { get; set; } = "sql";

    /// <summary>SQL（kind=sql 必填；kind=search 留空）.</summary>
    public string SqlText { get; set; } = string.Empty;

    /// <summary>搜索状态 JSON（kind=search 必填：domain/query/rangeMs；kind=sql 留空）.</summary>
    public string Definition { get; set; } = string.Empty;

    /// <summary>描述.</summary>
    public string Description { get; set; } = string.Empty;

    /// <inheritdoc/>
    public long ContextUserId { get; set; }

    /// <inheritdoc/>
    public string ContextUserRole { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SaveQueryCommand> validate)
    {
        validate.RuleFor(x => x.Name).NotEmpty().MaximumLength(64);
        validate.RuleFor(x => x.Kind).Must(kind => kind is "sql" or "search" or "analytics").WithMessage("kind 仅允许 sql/search/analytics");
        // kind=sql 校验 SQL 文本；kind=search 校验搜索状态 JSON，二者互斥
        validate.RuleFor(x => x.SqlText).NotEmpty().MaximumLength(200_000).When(x => x.Kind == "sql");
        validate.RuleFor(x => x.Definition).NotEmpty().MaximumLength(16_384).When(x => x.Kind is "search" or "analytics");
        validate.RuleFor(x => x.Description).MaximumLength(256);
    }
}

/// <summary>
/// 保存查询列表.
/// </summary>
public class QuerySavedQueriesCommand : PagedParameter, IRequest<PagedResult<SavedQueryItem>>
{
    /// <summary>类型过滤（可空：sql/search）.</summary>
    public string? Kind { get; set; }
}

/// <summary>
/// 按 id 查询保存项.
/// </summary>
public class GetSavedQueryCommand : IRequest<SavedQueryItem>, IModelValidator<GetSavedQueryCommand>
{
    /// <summary>id.</summary>
    public long Id { get; set; }

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<GetSavedQueryCommand> validate)
    {
        validate.RuleFor(x => x.Id).GreaterThan(0);
    }
}

/// <summary>
/// 删除保存的查询.
/// </summary>
public class DeleteSavedQueryCommand : IRequest<EmptyCommandResponse>, IUserIdContext
{
    /// <summary>id.</summary>
    public long Id { get; set; }

    /// <inheritdoc/>
    public long ContextUserId { get; set; }

    /// <inheritdoc/>
    public string ContextUserRole { get; set; } = string.Empty;
}

/// <summary>
/// 执行保存的查询.
/// </summary>
public class ExecuteSavedQueryCommand : IRequest<AnalyticsQueryResponse>, IModelValidator<ExecuteSavedQueryCommand>
{
    /// <summary>id.</summary>
    public long Id { get; set; }

    /// <summary>行数上限.</summary>
    public int RowLimit { get; set; } = 1000;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<ExecuteSavedQueryCommand> validate)
    {
        validate.RuleFor(x => x.RowLimit).InclusiveBetween(1, 10_000);
    }
}
