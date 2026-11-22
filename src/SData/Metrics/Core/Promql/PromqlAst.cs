namespace SData.Metrics.Promql;

/// <summary>
/// PromQL 子集 AST（T710；不支持二元运算/子查询/histogram_quantile，二期扩展）.
/// </summary>
public abstract record PromqlNode
{
    /// <summary>
    /// 位置起点（字符偏移，错误定位用）.
    /// </summary>
    public int Position { get; init; }
}

/// <summary>
/// 向量选择器：metric{label="v"}[5m] [offset 3m]；Range 为空表示即时向量.
/// </summary>
public sealed record PromqlVectorSelector(
    string MetricName,
    IReadOnlyList<PromqlLabelMatcher> Matchers,
    TimeSpan? Range,
    TimeSpan? Offset) : PromqlNode;

/// <summary>
/// 标签匹配器（= != =~ !~）.
/// </summary>
public sealed record PromqlLabelMatcher(string Label, string Operator, string Value);

/// <summary>
/// 函数调用（子集：rate）.
/// </summary>
public sealed record PromqlCall(string Function, PromqlNode Argument) : PromqlNode;

/// <summary>
/// 聚合表达式（sum/avg/min/max/count/topk/bottomk，可选 by/without）.
/// </summary>
public sealed record PromqlAggregate(
    string Operator,
    PromqlNode Argument,
    IReadOnlyList<string>? GroupingLabels,
    bool Without,
    int? Param) : PromqlNode;

/// <summary>
/// 数字字面量（topk/bottomk 参数位置）.
/// </summary>
public sealed record PromqlNumber(double Value) : PromqlNode;
