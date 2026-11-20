using SData.Infra.Models;
using SData.Store.Models;

namespace SData.Store;

/// <summary>
/// 日志存储.
/// </summary>
public interface ILogStore
{
    /// <summary>
    /// 查询日志.
    /// </summary>
    /// <param name="query">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>日志列表.</returns>
    Task<IReadOnlyList<LogRecord>> QueryAsync(LogQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出已有日志的服务名.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>服务名列表.</returns>
    Task<IReadOnlyList<string>> ListServicesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 链路存储.
/// </summary>
public interface ITraceStore
{
    /// <summary>
    /// 查询 trace 列表.
    /// </summary>
    /// <param name="query">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>trace 概要列表.</returns>
    Task<IReadOnlyList<TraceSummary>> QueryAsync(TraceQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取完整 trace（全部 span）.
    /// </summary>
    /// <param name="traceId">trace id.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>span 列表.</returns>
    Task<IReadOnlyList<SpanRecord>> GetTraceAsync(string traceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// 指标存储.
/// </summary>
public interface IMetricStore
{
    /// <summary>
    /// 列出指标名.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>指标名列表.</returns>
    Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 区间聚合查询.
    /// </summary>
    /// <param name="query">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>序列.</returns>
    Task<SeriesResponse> QueryRangeAsync(MetricSeriesQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// 遥测存储.
/// </summary>
public interface ITelemetryStore
{
    /// <summary>
    /// 区间聚合查询.
    /// </summary>
    /// <param name="query">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>序列.</returns>
    Task<SeriesResponse> QueryAsync(TelemetryQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设备各信号最新值.
    /// </summary>
    /// <param name="deviceKey">设备标识.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>最新值列表.</returns>
    Task<IReadOnlyList<LatestValue>> LatestAsync(string deviceKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// DuckDB 分析查询执行器（只读、受限）.
/// </summary>
public interface IAnalyticsExecutor
{
    /// <summary>执行受限的本地热数据告警计数；取消须中断原生查询.</summary>
    Task<AlertCountResult> ExecuteAlertCountAsync(AlertCountQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("此执行器不支持告警计数");

    /// <summary>
    /// 执行只读 SELECT 查询.
    /// </summary>
    /// <param name="sql">SQL（仅 SELECT/WITH 单语句）.</param>
    /// <param name="rowLimit">行数上限.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>结果.</returns>
    Task<AnalyticsQueryResult> ExecuteAsync(string sql, int rowLimit = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行统一搜索 SQL 直查：{{logs}} 等表占位符展开为区间内现存按天表 UNION ALL，
    /// $start/$end 以参数绑定（值来自调用方时间范围，非用户文本），其余 $name 引用以参数字典绑定；其余约束同 ExecuteAsync.
    /// </summary>
    /// <param name="sql">用户 SQL（仅 SELECT/WITH 单语句）.</param>
    /// <param name="from">起始时间（含）.</param>
    /// <param name="to">结束时间（不含）.</param>
    /// <param name="rowLimit">行数上限.</param>
    /// <param name="parameters">命名参数字典（$name → 值；可空）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>结果；区间内占位符无现存天表时为空结果.</returns>
    Task<AnalyticsQueryResult> ExecuteSearchAsync(string sql, DateTimeOffset from, DateTimeOffset to, int rowLimit = 500, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// 存储信息（监控用）.
/// </summary>
public interface IStoreInfo
{
    /// <summary>
    /// 统计表行数.
    /// </summary>
    /// <param name="tableName">表名.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>行数.</returns>
    Task<long> CountTableAsync(string tableName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出指定前缀的表名.
    /// </summary>
    /// <param name="prefix">前缀.</param>
    /// <returns>表名列表.</returns>
    IReadOnlyList<string> GetTableNames(string prefix);
}
