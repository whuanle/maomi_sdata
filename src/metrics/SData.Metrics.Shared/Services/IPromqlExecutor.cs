using SData.Store;

namespace SData.Metrics;

/// <summary>
/// PromQL 查询执行接口（实现在 Metrics.Core；跨层经接口消费）.
/// </summary>
public interface IPromqlExecutor
{
    /// <summary>
    /// 执行 PromQL 查询并返回 Prometheus 形状的序列.
    /// </summary>
    /// <param name="query">PromQL 表达式.</param>
    /// <param name="from">起始（含）.</param>
    /// <param name="to">结束（不含）.</param>
    /// <param name="step">分桶步长（null = 即时查询，取区间末端值）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>序列结果.</returns>
    Task<PromqlResult> ExecuteAsync(string query, DateTimeOffset from, DateTimeOffset to, TimeSpan? step, CancellationToken cancellationToken = default);
}

/// <summary>
/// PromQL 查询结果（Prometheus JSON 形状的数据部分）.
/// </summary>
public class PromqlResult
{
    /// <summary>vector（即时）或 matrix（区间）.</summary>
    public string ResultType { get; set; } = "vector";

    /// <summary>序列集合.</summary>
    public List<PromqlSeries> Result { get; set; } = [];
}

/// <summary>
/// 序列（metric 标签集 + 值点）.
/// </summary>
public class PromqlSeries
{
    /// <summary>标签集（含 __name__）.</summary>
    public Dictionary<string, string> Metric { get; set; } = new();

    /// <summary>即时值（vector）.</summary>
    public (DateTimeOffset Time, double Value)? Value { get; set; }

    /// <summary>区间值（matrix）.</summary>
    public List<(DateTimeOffset Time, double Value)> Values { get; set; } = [];
}

/// <summary>
/// PromQL 解析异常（位置定位 + 中文消息；Controller 层转 Prometheus 风格 error）.
/// </summary>
public class PromqlParseException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PromqlParseException"/> class.
    /// </summary>
    /// <param name="position">字符位置.</param>
    /// <param name="message">中文消息.</param>
    public PromqlParseException(int position, string message)
        : base($"第 {position + 1} 个字符附近：{message}")
    {
        Position = position;
    }

    /// <summary>
    /// 字符位置.
    /// </summary>
    public int Position { get; }
}
