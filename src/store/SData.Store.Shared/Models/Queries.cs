namespace SData.Store.Models;

/// <summary>
/// 聚合方式.
/// </summary>
public enum MetricAggregation
{
    /// <summary>平均.</summary>
    Avg,

    /// <summary>求和.</summary>
    Sum,

    /// <summary>最大.</summary>
    Max,

    /// <summary>最小.</summary>
    Min,

    /// <summary>计数.</summary>
    Count,

    /// <summary>最后值.</summary>
    Last,

    /// <summary>P95 分位.</summary>
    P95,
}

/// <summary>
/// 日志查询.
/// </summary>
public class LogQuery
{
    /// <summary>开始时间.</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>结束时间.</summary>
    public DateTimeOffset End { get; set; }

    /// <summary>服务名（精确）.</summary>
    public string? Service { get; set; }

    /// <summary>严重级别（精确，逗号分隔多个）.</summary>
    public string? Severity { get; set; }

    /// <summary>正文关键字（LIKE 包含匹配）.</summary>
    public string? Search { get; set; }

    /// <summary>日志来源（ES Bulk 索引名，精确）.</summary>
    public string? Source { get; set; }

    /// <summary>返回条数上限.</summary>
    public int Limit { get; set; } = 100;
}

/// <summary>
/// trace 概要.
/// </summary>
public class TraceSummary
{
    /// <summary>trace id.</summary>
    public string TraceId { get; set; } = string.Empty;

    /// <summary>根服务.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>根 span 名称.</summary>
    public string RootName { get; set; } = string.Empty;

    /// <summary>span 数.</summary>
    public long SpanCount { get; set; }

    /// <summary>错误 span 数.</summary>
    public long ErrorCount { get; set; }

    /// <summary>开始时间.</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>总耗时（微秒）.</summary>
    public long DurationUs { get; set; }
}

/// <summary>
/// trace 列表查询.
/// </summary>
public class TraceQuery
{
    /// <summary>开始时间.</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>结束时间.</summary>
    public DateTimeOffset End { get; set; }

    /// <summary>服务名（精确）.</summary>
    public string? Service { get; set; }

    /// <summary>最小耗时（微秒）.</summary>
    public long? MinDurationUs { get; set; }

    /// <summary>仅包含错误.</summary>
    public bool HasError { get; set; }

    /// <summary>返回条数上限.</summary>
    public int Limit { get; set; } = 50;
}

/// <summary>
/// 指标区间查询.
/// </summary>
public class MetricSeriesQuery
{
    /// <summary>指标名.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>标签过滤（精确匹配）.</summary>
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>开始时间.</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>结束时间.</summary>
    public DateTimeOffset End { get; set; }

    /// <summary>聚合方式.</summary>
    public MetricAggregation Aggregation { get; set; } = MetricAggregation.Avg;

    /// <summary>降采样步长（秒）.</summary>
    public int StepSeconds { get; set; } = 60;
}

/// <summary>
/// 遥测区间查询.
/// </summary>
public class TelemetryQuery
{
    /// <summary>设备标识.</summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>信号名（空 = 全部）.</summary>
    public List<string> Signals { get; set; } = new();

    /// <summary>开始时间.</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>结束时间.</summary>
    public DateTimeOffset End { get; set; }

    /// <summary>聚合方式.</summary>
    public MetricAggregation Aggregation { get; set; } = MetricAggregation.Avg;

    /// <summary>降采样步长（秒）.</summary>
    public int StepSeconds { get; set; } = 60;
}

/// <summary>
/// 序列数据点.
/// </summary>
public class SeriesPoint
{
    /// <summary>桶起始时间（毫秒时间戳）.</summary>
    public long TimestampMs { get; set; }

    /// <summary>值.</summary>
    public double? Value { get; set; }
}

/// <summary>
/// 序列查询响应.
/// </summary>
public class SeriesResponse
{
    /// <summary>序列（每条 = 一组标签 + 点集）.</summary>
    public List<Series> SeriesList { get; set; } = new();

    /// <summary>
    /// 单条序列.
    /// </summary>
    public class Series
    {
        /// <summary>信号/指标名.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>分组标签（JSON 对象字符串）.</summary>
        public string LabelsJson { get; set; } = "{}";

        /// <summary>数据点.</summary>
        public List<SeriesPoint> Points { get; set; } = new();
    }
}

/// <summary>
/// 最新值.
/// </summary>
public class LatestValue
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
