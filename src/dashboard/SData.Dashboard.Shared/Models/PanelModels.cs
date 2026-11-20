using System.Text.Json;
using MediatR;
using SData.Infra.Exceptions;
using SData.Infra.Models;
using SData.Infra.Services;
using SData.Store.QueryDsl;

namespace SData.Dashboard;

/// <summary>
/// 仪表盘配置 JSON 序列化选项（camelCase 输出 + 大小写不敏感解析，兼容手写与旧配置）.
/// </summary>
public static class DashboardJson
{
    /// <summary>共享选项.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>
/// 面板图表类型（type 白名单）.
/// </summary>
public static class PanelTypes
{
    /// <summary>折线.</summary>
    public const string Line = "line";

    /// <summary>面积.</summary>
    public const string Area = "area";

    /// <summary>柱状.</summary>
    public const string Bar = "bar";

    /// <summary>数值卡.</summary>
    public const string Stat = "stat";

    /// <summary>表格.</summary>
    public const string Table = "table";

    /// <summary>静态 Markdown.</summary>
    public const string Markdown = "markdown";

    /// <summary>标签页容器.</summary>
    public const string Group = "group";

    /// <summary>Markdown 正文长度上限.</summary>
    public const int MarkdownMaxLength = 50_000;

    /// <summary>全部类型.</summary>
    public static readonly string[] All = [Line, Area, Bar, Stat, Table, "pie", "heatmap", Markdown, Group];
}

/// <summary>
/// 面板查询形态（query.kind 白名单）：kind 决定分派到的执行器.
/// </summary>
public static class PanelQueryKinds
{
    /// <summary>原生指标序列（IMetricStore.QueryRangeAsync）.</summary>
    public const string Metric = "metric";

    /// <summary>统一 DSL 聚合（日志/链路/指标/IoT 派生指标，IQueryDslExecutor）.</summary>
    public const string Dsl = "dsl";

    /// <summary>PromQL 子集表达式（IPromqlExecutor）.</summary>
    public const string Promql = "promql";

    /// <summary>分析 SQL（IAnalyticsExecutor，只读单语句 SELECT；旧面板协议兼容）.</summary>
    public const string Sql = "sql";

    /// <summary>全部形态.</summary>
    public const string Custom = "custom";

    public static readonly string[] All = [Metric, Dsl, Promql, Sql, Custom];
}

/// <summary>
/// 面板时间范围配置（仪表盘级）：preset 相对最近时长，或 custom 显式区间.
/// </summary>
public class PanelTimeConfig
{
    /// <summary>预设 key（1m/5m/15m/30m/1h/3h/6h/12h/1d/2d/7d/custom；与前端 TimeRangePicker 预设对齐）.</summary>
    public string Preset { get; set; } = "1h";

    /// <summary>显式起始（preset=custom 时生效，ISO8601）.</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>显式结束（preset=custom 时生效）.</summary>
    public DateTimeOffset? To { get; set; }
}

/// <summary>
/// 面板时间范围解析（请求参数 &gt; 配置 preset &gt; 缺省最近 1h）.
/// </summary>
public static class PanelRange
{
    private static readonly Dictionary<string, TimeSpan> Presets = new(StringComparer.Ordinal)
    {
        ["1m"] = TimeSpan.FromMinutes(1),
        ["5m"] = TimeSpan.FromMinutes(5),
        ["15m"] = TimeSpan.FromMinutes(15),
        ["30m"] = TimeSpan.FromMinutes(30),
        ["45m"] = TimeSpan.FromMinutes(45),
        ["1h"] = TimeSpan.FromHours(1),
        ["3h"] = TimeSpan.FromHours(3),
        ["6h"] = TimeSpan.FromHours(6),
        ["12h"] = TimeSpan.FromHours(12),
        ["1d"] = TimeSpan.FromDays(1),
        ["2d"] = TimeSpan.FromDays(2),
        ["7d"] = TimeSpan.FromDays(7),
    };

    /// <summary>
    /// 解析查询区间.
    /// </summary>
    /// <param name="config">仪表盘时间配置（可空）.</param>
    /// <param name="requestFrom">请求覆盖起始.</param>
    /// <param name="requestTo">请求覆盖结束.</param>
    /// <returns>区间（from &lt; to）.</returns>
    public static (DateTimeOffset From, DateTimeOffset To) Resolve(PanelTimeConfig? config, DateTimeOffset? requestFrom, DateTimeOffset? requestTo)
    {
        var to = requestTo ?? (config?.Preset == "custom" ? config.To : null) ?? DateTimeOffset.UtcNow;
        var from = requestFrom ?? (config?.Preset == "custom" ? config.From : null) ?? to - PresetSpan(config);
        if (from >= to)
        {
            throw BusinessException.Throw(400, "时间范围 from 必须早于 to");
        }

        return (from, to);
    }

    private static TimeSpan PresetSpan(PanelTimeConfig? config)
    {
        if (config == null)
        {
            return TimeSpan.FromHours(1);
        }

        if (config.Preset == "custom" && config.From.HasValue && config.To.HasValue)
        {
            return config.To.Value - config.From.Value;
        }

        return Presets.GetValueOrDefault(config.Preset, TimeSpan.FromHours(1));
    }
}

/// <summary>
/// 面板布局（12 列栅格 + 高度档位）.
/// </summary>
public class PanelLayoutConfig
{
    /// <summary>v2 网格横坐标.</summary>
    public int? X { get; set; }

    /// <summary>v2 网格纵坐标.</summary>
    public int? Y { get; set; }

    /// <summary>栅格宽度（1–12）.</summary>
    public int W { get; set; } = 4;

    /// <summary>高度档位（1–8，每档约 100px）.</summary>
    public int H { get; set; } = 4;
}

/// <summary>
/// 面板查询配置（kind 多态平铺；仅 kind 对应的字段生效）.
/// </summary>
public class PanelQueryConfig
{
    /// <summary>查询形态：metric / dsl / promql / sql.</summary>
    public string Kind { get; set; } = PanelQueryKinds.Metric;

    /// <summary>自定义 SQL 与表达式查询图.</summary>
    public CustomPanelDefinition? Custom { get; set; }

    // ---- kind = metric：原生指标 ----

    /// <summary>指标名（metric 形态必填）.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>标签精确过滤（metric 形态；≤10 组）.</summary>
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>聚合方式：avg/sum/max/min/count/last/p95（metric 形态）.</summary>
    public string Aggregation { get; set; } = "avg";

    // ---- kind = metric / promql 共用 ----

    /// <summary>降采样步长（秒；promql 形态为 query_range 的 step）.</summary>
    public int StepSeconds { get; set; } = 60;

    /// <summary>序列数量上限（防标签基数爆炸，1–100）.</summary>
    public int MaxSeries { get; set; } = 20;

    // ---- kind = dsl：统一 DSL ----

    /// <summary>数据域：logs/traces/metrics/iot（dsl 形态）.</summary>
    public string Domain { get; set; } = "logs";

    /// <summary>DSL 查询体（dsl 形态必填；range 由仪表盘时间覆盖，不使用存储值）.</summary>
    public QueryDslModel? Dsl { get; set; }

    // ---- kind = promql ----

    /// <summary>PromQL 表达式（promql 形态必填）.</summary>
    public string Expr { get; set; } = string.Empty;

    // ---- kind = sql ----

    /// <summary>分析 SQL（sql 形态；与 QueryId 二选一）.</summary>
    public string Sql { get; set; } = string.Empty;

    /// <summary>保存的查询 id（sql 形态；与 Sql 二选一）.</summary>
    public long QueryId { get; set; }

    /// <summary>SQL 绑定参数（时间参数由查询范围提供）.</summary>
    public Dictionary<string, object?> Parameters { get; set; } = new();

    /// <summary>按当前查询时间跨度绑定 chart_bucket.</summary>
    public bool AutoGranularity { get; set; }

    /// <summary>数值面板的独立背景趋势查询.</summary>
    public PanelTrendQuery? NumberTrend { get; set; }
}

/// <summary>背景趋势查询，沿用分析只读执行通道.</summary>
public class PanelTrendQuery
{
    /// <summary>参数化 SQL.</summary>
    public string Sql { get; set; } = string.Empty;

    /// <summary>绑定参数.</summary>
    public Dictionary<string, object?> Parameters { get; set; } = new();
}

/// <summary>
/// 面板配置（configJson.panels[] 元素；Query 为空时按旧协议 Sql/QueryId 处理）.
/// </summary>
public class PanelConfig
{
    /// <summary>面板 id（面板内唯一）.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>标题.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>图表类型：line/area/bar/stat/table.</summary>
    public string Type { get; set; } = PanelTypes.Line;

    /// <summary>栅格布局.</summary>
    public PanelLayoutConfig Layout { get; set; } = new();

    /// <summary>Group 有序成员，不支持嵌套容器.</summary>
    public List<PanelConfig>? Panels { get; set; }

    /// <summary>Group 默认激活的成员 ID.</summary>
    public string? DefaultPanelId { get; set; }

    /// <summary>数值单位：short/ms/bytes/percent/ops（前端格式化）.</summary>
    public string Unit { get; set; } = "short";

    /// <summary>是否显示图例.</summary>
    public bool ShowLegend { get; set; } = true;

    /// <summary>静态 Markdown 正文，不依赖查询.</summary>
    public string? Markdown { get; set; }

    /// <summary>独立版本的运行时展示定义.</summary>
    public JsonElement? Visualization { get; set; }

    /// <summary>仅用于恢复编辑工作区，不参与查询执行.</summary>
    public JsonElement? Editor { get; set; }

    /// <summary>Explorer 展示配置（兼容保留）.</summary>
    public JsonElement? Explorer { get; set; }

    /// <summary>查询配置（新协议；空 = 旧协议回退）.</summary>
    public PanelQueryConfig? Query { get; set; }

    // ---- 旧协议字段（Query == null 时使用）----

    /// <summary>面板 SQL（旧协议）.</summary>
    public string? Sql { get; set; }

    /// <summary>保存的查询 id（旧协议）.</summary>
    public long QueryId { get; set; }

    /// <summary>行数上限（旧协议）.</summary>
    public int? RowLimit { get; set; }
}

/// <summary>
/// 仪表盘配置（configJson 反序列化目标；version=1 起新协议，缺 version 的旧配置兼容解析）.
/// </summary>
public class DashboardConfig
{
    /// <summary>协议版本.</summary>
    public int Version { get; set; } = 1;

    /// <summary>仪表盘级时间范围.</summary>
    public PanelTimeConfig? Time { get; set; }

    /// <summary>自动刷新间隔（秒；0 = 不刷新）.</summary>
    public int RefreshSeconds { get; set; }

    /// <summary>面板列表.</summary>
    public List<PanelConfig> Panels { get; set; } = new();
}

/// <summary>
/// 面板序列点.
/// </summary>
public class PanelSeriesPoint
{
    /// <summary>时间（毫秒时间戳）.</summary>
    public long TimestampMs { get; set; }

    /// <summary>值.</summary>
    public double? Value { get; set; }
}

/// <summary>
/// 面板序列（时间序列形态；Name + Labels 与 MetricStore.SeriesResponse 对齐）.
/// </summary>
public class PanelSeries
{
    /// <summary>序列名（指标名或聚合别名）.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分组标签（JSON 对象字符串）.</summary>
    public string Labels { get; set; } = "{}";

    /// <summary>数据点.</summary>
    public List<PanelSeriesPoint> Points { get; set; } = new();
}

/// <summary>
/// 校验面板查询配置（PreviewPanelCommand 与保存校验共用）.
/// </summary>
public static class PanelQueryValidator
{
    private static readonly string[] Aggregations = ["avg", "sum", "max", "min", "count", "last", "p95"];

    private static readonly string[] Domains = ["logs", "traces", "metrics", "iot"];

    /// <summary>
    /// 校验查询配置；合法返回 null，否则返回错误消息.
    /// </summary>
    /// <param name="query">查询配置.</param>
    /// <returns>错误消息或 null.</returns>
    public static string? Validate(PanelQueryConfig? query)
    {
        if (query == null)
        {
            return "缺少查询配置（query）";
        }

        if (!PanelQueryKinds.All.Contains(query.Kind))
        {
            return $"未知查询形态：{query.Kind}（可选 metric/dsl/promql/sql）";
        }

        var check = query.Kind switch
        {
            PanelQueryKinds.Metric => ValidateMetric(query),
            PanelQueryKinds.Dsl => ValidateDsl(query),
            PanelQueryKinds.Promql => ValidatePromql(query),
            PanelQueryKinds.Custom => CustomPanelDefinition.Validate(query.Custom),
            _ => ValidateSql(query),
        };
        return check;
    }

    private static string? ValidateMetric(PanelQueryConfig query)
    {
        if (string.IsNullOrWhiteSpace(query.Name))
        {
            return "metric 查询必须指定指标名（name）";
        }

        if (query.Name.Length > 128)
        {
            return "指标名长度不能超过 128";
        }

        if (!Aggregations.Contains(query.Aggregation))
        {
            return $"不支持的聚合方式：{query.Aggregation}";
        }

        if (query.StepSeconds is < 10 or > 86400)
        {
            return "步长需在 10–86400 秒之间";
        }

        if (query.MaxSeries is < 1 or > 100)
        {
            return "序列上限需在 1–100 之间";
        }

        if (query.Labels.Count > 10)
        {
            return "标签过滤最多 10 组";
        }

        return null;
    }

    private static string? ValidateDsl(PanelQueryConfig query)
    {
        if (!Domains.Contains(query.Domain))
        {
            return $"未知数据域：{query.Domain}（可选 logs/traces/metrics/iot）";
        }

        if (query.Dsl == null)
        {
            return "dsl 查询必须提供查询体（dsl）";
        }

        if (query.Dsl.Bucket == null && query.Dsl.Aggregations.Count == 0)
        {
            return "dsl 查询需配置时间分桶（bucket）或聚合（aggregations）至少其一";
        }

        if (query.Dsl.GroupBy.Count > 5)
        {
            return "分组字段最多 5 个";
        }

        if (query.MaxSeries is < 1 or > 100)
        {
            return "序列上限需在 1–100 之间";
        }

        return null;
    }

    private static string? ValidatePromql(PanelQueryConfig query)
    {
        if (string.IsNullOrWhiteSpace(query.Expr))
        {
            return "promql 查询必须提供表达式（expr）";
        }

        if (query.Expr.Length > 4000)
        {
            return "PromQL 表达式长度不能超过 4000";
        }

        if (query.StepSeconds is < 10 or > 86400)
        {
            return "步长需在 10–86400 秒之间";
        }

        if (query.MaxSeries is < 1 or > 100)
        {
            return "序列上限需在 1–100 之间";
        }

        return null;
    }

    private static string? ValidateSql(PanelQueryConfig query)
    {
        if (query.QueryId > 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(query.Sql))
        {
            return "sql 查询必须提供 SQL 或保存的查询 id（queryId）";
        }

        return null;
    }
}

/// <summary>
/// 预览面板查询（面板编辑器实时预览；未保存的面板配置即可执行）.
/// </summary>
public class PreviewPanelCommand : IRequest<PanelData>, IUserIdContext, IModelValidator<PreviewPanelCommand>
{
    /// <summary>面板配置.</summary>
    public PanelConfig Panel { get; set; } = new();

    /// <summary>起始时间（缺省 = 结束前 1 小时）.</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>结束时间（缺省 = 当前时间）.</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>行数上限（表格形态）.</summary>
    public int RowLimit { get; set; } = 500;

    /// <inheritdoc/>
    public long ContextUserId { get; set; }

    /// <inheritdoc/>
    public string ContextUserRole { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<PreviewPanelCommand> validate)
    {
        validate.RuleFor(x => x.RowLimit).InclusiveBetween(1, 10_000);
        validate.RuleFor(x => x.Panel.Title).MaximumLength(128);
        validate.RuleFor(x => x.Panel.Type).Must(x => PanelTypes.All.Contains(x))
            .WithMessage($"未知图表类型（可选 {string.Join("/", PanelTypes.All)}）");
        validate.RuleFor(x => x.Panel.Layout.W).InclusiveBetween(1, 12);
        validate.RuleFor(x => x.Panel.Layout.H).InclusiveBetween(1, 8);
        validate.RuleFor(x => x.From)
            .Must((command, from) => from == null || command.To == null || from < command.To)
            .WithMessage("时间范围 from 必须早于 to");
        validate.RuleFor(x => x.Panel)
            .Must(x => x.Type == PanelTypes.Markdown || PanelQueryValidator.Validate(x.Query) == null)
            .WithMessage(x => PanelQueryValidator.Validate(x.Panel.Query) ?? string.Empty);
        validate.RuleFor(x => x.Panel)
            .Must(x => DashboardConfigValidator.PanelDefinitionError(x) == null)
            .WithMessage(x => DashboardConfigValidator.PanelDefinitionError(x.Panel) ?? string.Empty);
        validate.RuleFor(x => x.Panel.Markdown).MaximumLength(PanelTypes.MarkdownMaxLength)
            .WithMessage("Markdown 正文不能超过 50000 个字符");
    }
}
