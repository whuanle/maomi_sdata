using System.Globalization;

namespace SData.Store.Core;

/// <summary>
/// 按天分表的命名与建表 SQL（日志/链路/指标/遥测均按天分表，保留策略直接 DROP）.
/// </summary>
public static class TimeTables
{
    /// <summary>消息消费统计日表.</summary>
    public const string MqConsumptionPrefix = "mq_consumption_";

    /// <summary>日志表前缀.</summary>
    public const string LogsPrefix = "logs_";

    /// <summary>链路表前缀.</summary>
    public const string TracesPrefix = "traces_";

    /// <summary>指标表前缀.</summary>
    public const string MetricsPrefix = "metrics_";

    /// <summary>遥测表前缀.</summary>
    public const string IotPrefix = "iot_";

    /// <summary>RUM 事件表前缀.</summary>
    public const string RumEventPrefix = "rum_event_";

    /// <summary>RUM 会话表前缀.</summary>
    public const string RumSessionPrefix = "rum_session_";

    /// <summary>RUM 回放表前缀.</summary>
    public const string RumReplayPrefix = "rum_replay_";

    /// <summary>指标 5 分钟 rollup 汇总表（全局表，非天分表；契约 C6）.</summary>
    public const string Rollup5mTable = "metrics_rollup_5m";

    /// <summary>指标 1 小时 rollup 汇总表（全局表，非天分表；契约 C6）.</summary>
    public const string Rollup1hTable = "metrics_rollup_1h";

    /// <summary>
    /// 日期格式（yyyyMMdd）.
    /// </summary>
    public const string DayFormat = "yyyyMMdd";

    /// <summary>
    /// 生成表名.
    /// </summary>
    /// <param name="prefix">前缀.</param>
    /// <param name="time">时间.</param>
    /// <returns>表名.</returns>
    public static string Name(string prefix, DateTimeOffset time)
    {
        return prefix + time.UtcDateTime.ToString(DayFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>冻结批次表名：日期保留在末尾，以便复用日期解析.</summary>
    internal static string ArchiveName(string prefix, DateTimeOffset day) => $"{prefix}archive_{Guid.NewGuid():N}_{day.UtcDateTime:yyyyMMdd}";

    internal static bool IsArchive(string table) => table.Contains("_archive_", StringComparison.Ordinal);

    internal static string ArchiveId(string table) => table.Split('_')[^2];

    /// <summary>
    /// 解析表名中的日期（非法返回 null）.
    /// </summary>
    /// <param name="tableName">表名.</param>
    /// <returns>日期.</returns>
    public static DateTimeOffset? ParseDay(string tableName)
    {
        if (tableName.Length < 8)
        {
            return null;
        }

        var dayPart = tableName[^8..];
        if (!DateTimeOffset.TryParseExact(dayPart, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var day))
        {
            return null;
        }

        return day;
    }

    /// <summary>
    /// 生成建表 SQL（含链路 trace_id 索引）.
    /// </summary>
    /// <param name="tableName">表名.</param>
    /// <returns>建表语句列表.</returns>
    public static IReadOnlyList<string> CreateSql(string tableName)
    {
        if (tableName.StartsWith(MqConsumptionPrefix, StringComparison.Ordinal))
        {
            return [$"CREATE TABLE IF NOT EXISTS {tableName} (ts TIMESTAMP NOT NULL, instance_id BIGINT NOT NULL, session_id VARCHAR NOT NULL, rule VARCHAR NOT NULL, match_mode VARCHAR NOT NULL, state VARCHAR NOT NULL, received_messages BIGINT, received_bytes BIGINT, persisted_records BIGINT, parse_failures BIGINT, skipped_messages BIGINT, confirmed_messages BIGINT, confirmation_failures BIGINT, sampling_gaps BIGINT, diagnostics VARCHAR)"];
        }

        if (tableName.StartsWith(LogsPrefix, StringComparison.Ordinal))
        {
            return
            [
                $"CREATE TABLE IF NOT EXISTS {tableName} (id BIGINT, ts TIMESTAMP NOT NULL, service VARCHAR NOT NULL, severity VARCHAR NOT NULL, body VARCHAR NOT NULL, trace_id VARCHAR, span_id VARCHAR, attributes VARCHAR, resource VARCHAR, source VARCHAR)",
            ];
        }

        if (tableName.StartsWith(TracesPrefix, StringComparison.Ordinal))
        {
            return
            [
                $"CREATE TABLE IF NOT EXISTS {tableName} (trace_id VARCHAR NOT NULL, span_id VARCHAR NOT NULL, parent_span_id VARCHAR, ts TIMESTAMP NOT NULL, duration_us BIGINT NOT NULL, service VARCHAR NOT NULL, name VARCHAR NOT NULL, kind VARCHAR NOT NULL, status VARCHAR NOT NULL, attributes VARCHAR, events VARCHAR, resource VARCHAR)",
                $"CREATE INDEX IF NOT EXISTS {tableName}_tid_idx ON {tableName} (trace_id)",
            ];
        }

        if (tableName.StartsWith(MetricsPrefix, StringComparison.Ordinal))
        {
            return
            [
                $"CREATE TABLE IF NOT EXISTS {tableName} (name VARCHAR NOT NULL, ts TIMESTAMP NOT NULL, value DOUBLE NOT NULL, labels VARCHAR, kind VARCHAR NOT NULL)",
            ];
        }

        if (tableName.StartsWith(IotPrefix, StringComparison.Ordinal))
        {
            return
            [
                $"CREATE TABLE IF NOT EXISTS {tableName} (device_key VARCHAR NOT NULL, signal VARCHAR NOT NULL, ts TIMESTAMP NOT NULL, num_value DOUBLE, str_value VARCHAR, quality INTEGER NOT NULL)",
            ];
        }

        if (tableName.StartsWith(RumEventPrefix, StringComparison.Ordinal))
        {
            return
            [
                $"CREATE TABLE IF NOT EXISTS {tableName} (ts TIMESTAMP NOT NULL, project_key VARCHAR NOT NULL, session_id VARCHAR NOT NULL, event_type VARCHAR NOT NULL, event_name VARCHAR, page_path VARCHAR, value DOUBLE, payload VARCHAR)",
            ];
        }

        if (tableName.StartsWith(RumSessionPrefix, StringComparison.Ordinal))
        {
            return
            [
                $"CREATE TABLE IF NOT EXISTS {tableName} (ts TIMESTAMP NOT NULL, project_key VARCHAR NOT NULL, session_id VARCHAR NOT NULL, user_agent VARCHAR, domain VARCHAR)",
            ];
        }

        if (tableName.StartsWith(RumReplayPrefix, StringComparison.Ordinal))
        {
            return
            [
                $"CREATE TABLE IF NOT EXISTS {tableName} (ts TIMESTAMP NOT NULL, project_key VARCHAR NOT NULL, session_id VARCHAR NOT NULL, seq BIGINT NOT NULL, blob_base64 VARCHAR NOT NULL)",
            ];
        }

        throw new ArgumentException($"Unknown table prefix: {tableName}", nameof(tableName));
    }
}
