using System.Text.Json;

namespace SData.Store.QueryDsl;

/// <summary>
/// 查询数据域（与 TimeTables 前缀族一一对应）.
/// </summary>
public enum DataDomain
{
    /// <summary>日志（logs_ 天表族）.</summary>
    Logs,

    /// <summary>链路（traces_ 天表族）.</summary>
    Traces,

    /// <summary>指标（metrics_ 天表族）.</summary>
    Metrics,

    /// <summary>物联网遥测（iot_ 天表族）.</summary>
    Iot,

    /// <summary>RUM 页面浏览.</summary>
    RumSession,

    /// <summary>RUM 事件.</summary>
    RumEvent,

    /// <summary>RUM 回放分段.</summary>
    RumReplay,
}

/// <summary>
/// 统一查询 DSL 请求模型（契约 C1：POST /api/{domain}/search 请求体）.
/// </summary>
public class QueryDslModel
{
    /// <summary>
    /// 时间范围（必填，from &lt; to）.
    /// </summary>
    public QueryRange? Range { get; set; }

    /// <summary>
    /// 过滤条件（全部 AND 串联）.
    /// </summary>
    public List<QueryFilter> Filters { get; set; } = new();

    /// <summary>
    /// 分组字段（需与 aggregations 或 bucket 搭配）.
    /// </summary>
    public List<string> GroupBy { get; set; } = new();

    /// <summary>
    /// 聚合列表（count/avg/sum/min/max/quantile）.
    /// </summary>
    public List<QueryAggregation> Aggregations { get; set; } = new();

    /// <summary>
    /// 时间分桶直方图（可选）.
    /// </summary>
    public QueryBucket? Bucket { get; set; }

    /// <summary>
    /// 排序（聚合模式按输出别名，原始行模式按字段白名单）.
    /// </summary>
    public List<QuerySort> Sort { get; set; } = new();

    /// <summary>
    /// 行数上限（缺省 50，上限 10000）.
    /// </summary>
    public int? Limit { get; set; }
}

/// <summary>
/// 查询时间范围.
/// </summary>
public class QueryRange
{
    /// <summary>起始时间.</summary>
    public DateTimeOffset From { get; set; }

    /// <summary>结束时间.</summary>
    public DateTimeOffset To { get; set; }
}

/// <summary>
/// 过滤条件（op：eq/neq/in/prefix/match/gt/gte/lt/lte）.
/// </summary>
public class QueryFilter
{
    /// <summary>字段名（物理列或 attributes./resource./labels. 前缀的 JSON 路径）.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>操作符.</summary>
    public string Op { get; set; } = "eq";

    /// <summary>过滤值（eq/neq/prefix/match/gt/gte/lt/lte 为标量；in 为数组）.</summary>
    public JsonElement? Value { get; set; }
}

/// <summary>
/// 聚合定义.
/// </summary>
public class QueryAggregation
{
    /// <summary>聚合函数：count/avg/sum/min/max/quantile.</summary>
    public string Func { get; set; } = "count";

    /// <summary>聚合字段（count 可空；avg/sum/quantile 必须数值；min/max 允许数值或时间）.</summary>
    public string? Field { get; set; }

    /// <summary>quantile 分位数（0 &lt; q &lt;= 1，缺省 0.5）.</summary>
    public double? Q { get; set; }

    /// <summary>输出列别名（缺省用函数名）.</summary>
    public string Alias { get; set; } = string.Empty;
}

/// <summary>
/// 时间分桶直方图.
/// </summary>
public class QueryBucket
{
    /// <summary>分桶步长（如 30s/5m/1h/1d）.</summary>
    public string Step { get; set; } = "5m";

    /// <summary>桶内聚合函数（仅在 aggregations 为空时生效）.</summary>
    public string Func { get; set; } = "count";

    /// <summary>聚合字段（可选，语义同 QueryAggregation.Field）.</summary>
    public string? Field { get; set; }

    /// <summary>quantile 分位数（0 &lt; q &lt;= 1，缺省 0.5）.</summary>
    public double? Q { get; set; }
}

/// <summary>
/// 排序项.
/// </summary>
public class QuerySort
{
    /// <summary>排序字段.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>是否降序.</summary>
    public bool Desc { get; set; }
}

/// <summary>
/// 统一查询 DSL 响应（列式）：聚合模式列为 bucket/分组字段/聚合别名；原始行模式列为域物理列名.
/// </summary>
public class SearchDslResponse
{
    /// <summary>列名.</summary>
    public List<string> Columns { get; set; } = new();

    /// <summary>数据行（键=列名；时间值为 UTC DateTimeOffset）.</summary>
    public List<Dictionary<string, object?>> Rows { get; set; } = new();

    /// <summary>执行耗时（毫秒）.</summary>
    public long Took { get; set; }

    /// <summary>实际扫描的天表数（0 = 区间内无数据表）.</summary>
    public int TablesScanned { get; set; }
}

/// <summary>
/// 统一查询 DSL 执行接口（实现在 Store.Core；logs/traces/metrics/iot 四域 Handler 与后续 PromQL 查询复用）.
/// 含 MaxQueryDays 跨度约束与空表区间短路.
/// </summary>
public interface IQueryDslExecutor
{
    /// <summary>
    /// 编译并执行 DSL 查询.
    /// </summary>
    /// <param name="model">DSL 请求模型.</param>
    /// <param name="domain">数据域.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>列式响应.</returns>
    Task<SearchDslResponse> ExecuteAsync(QueryDslModel model, DataDomain domain, CancellationToken cancellationToken = default);
}

/// <summary>
/// 查询表解析接口（契约 C2）：本地天表 + 冷层 Parquet 路径集合；统一查询管线唯一表解析入口（异步全链路）.
/// </summary>
public interface ITableResolver
{
    /// <summary>
    /// 解析查询区间涉及的数据源.
    /// </summary>
    /// <param name="domain">数据域.</param>
    /// <param name="from">起始（含）.</param>
    /// <param name="to">结束（不含）.</param>
    /// <param name="searchKeyword">全文检索关键词（可选；用于词项索引候选分片裁剪，减少冷层下载/扫描范围）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>本地表集合与冷层路径.</returns>
    Task<ResolvedTables> ResolveAsync(DataDomain domain, DateTimeOffset from, DateTimeOffset to, string? searchKeyword = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// 表解析结果（契约 C2）：未配置 S3 时 ColdParquetGlobs 恒为空，查询层行为与现状一致.
/// </summary>
/// <param name="LocalTables">本地 DuckDB 天表名.</param>
/// <param name="ColdParquetGlobs">冷层 parquet 路径通配（s3://... 或本地路径；仅台账为空时兜底）.</param>
/// <param name="ColdFiles">冷层逐日精确路径（来自台账；非空时与 Glob 兜底互斥，避免重复扫描）.</param>
public sealed record ResolvedTables(IReadOnlyList<string> LocalTables, IReadOnlyList<string> ColdParquetGlobs)
{
    /// <summary>冷层逐日精确路径（台账驱动；缓存命中为本地缓存路径，未命中为 S3 直读路径或本地冷存路径）.</summary>
    public IReadOnlyList<string> ColdFiles { get; init; } = Array.Empty<string>();
}
