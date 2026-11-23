using System.Globalization;

namespace SData.Store.Core;

/// <summary>
/// 按天分表的命名与建表 SQL（日志/链路/指标/遥测均按天分表，保留策略直接 DROP）.
/// logs/traces 为「视图门面」天对象：物理数据在 data 窄表（resource 列字典编码为 resource_id），
/// resource 原文一天一行存于 res 字典表；天名（logs_yyyymmdd）本身是暴露旧宽列序的视图，
/// 查询层（LogStore/QueryDsl/归档 parquet union）零改动。旧版本遗留的物理宽表原样共存.
/// </summary>
public static class TimeTables
{
    /// <summary>消息消费统计日表.</summary>
    public const string MqConsumptionPrefix = "mq_consumption_";

    /// <summary>日志表前缀（视图门面天对象）.</summary>
    public const string LogsPrefix = "logs_";

    /// <summary>链路表前缀（视图门面天对象）.</summary>
    public const string TracesPrefix = "traces_";

    /// <summary>指标表前缀.</summary>
    public const string MetricsPrefix = "metrics_";

    /// <summary>遥测表前缀.</summary>
    public const string IotPrefix = "iot_";

    /// <summary>日志数据窄表前缀（resource 字典编码；不以 logs_ 开头避免前缀枚举碰撞）.</summary>
    public const string LogDataPrefix = "log_data_";

    /// <summary>日志 resource 字典表前缀.</summary>
    public const string LogResPrefix = "log_res_";

    /// <summary>链路数据窄表前缀.</summary>
    public const string TraceDataPrefix = "trace_data_";

    /// <summary>链路 resource 字典表前缀.</summary>
    public const string TraceResPrefix = "trace_res_";

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
    /// 生成天对象表名（logs/traces 为视图名）.
    /// </summary>
    /// <param name="prefix">前缀.</param>
    /// <param name="time">时间.</param>
    /// <returns>表名.</returns>
    public static string Name(string prefix, DateTimeOffset time)
    {
        return prefix + time.UtcDateTime.ToString(DayFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 该前缀的天对象是否为视图门面（数据在伴生窄表 + 字典表）.
    /// </summary>
    /// <param name="prefix">前缀.</param>
    /// <returns>true = 视图门面.</returns>
    public static bool IsViewBacked(string prefix)
    {
        return prefix is LogsPrefix or TracesPrefix;
    }

    /// <summary>
    /// 视图门面天对象的物理数据窄表名.
    /// </summary>
    /// <param name="prefix">天对象前缀（logs_/traces_）.</param>
    /// <param name="time">时间.</param>
    /// <returns>数据窄表名.</returns>
    public static string DataTableName(string prefix, DateTimeOffset time)
    {
        var dataPrefix = prefix == LogsPrefix ? LogDataPrefix : TraceDataPrefix;
        return Name(dataPrefix, time);
    }

    /// <summary>
    /// 视图门面天对象的 resource 字典表名.
    /// </summary>
    /// <param name="prefix">天对象前缀（logs_/traces_）.</param>
    /// <param name="time">时间.</param>
    /// <returns>字典表名.</returns>
    public static string ResTableName(string prefix, DateTimeOffset time)
    {
        var resPrefix = prefix == LogsPrefix ? LogResPrefix : TraceResPrefix;
        return Name(resPrefix, time);
    }

    /// <summary>
    /// 视图 SQL：暴露与旧宽表完全一致的列名与列序（查询层零改动）.
    /// </summary>
    /// <param name="viewName">视图名（天对象名或冻结快照名）.</param>
    /// <param name="dataTable">数据窄表名.</param>
    /// <param name="resTable">字典表名.</param>
    /// <param name="prefix">天对象前缀（logs_/traces_，决定列集）.</param>
    /// <returns>CREATE VIEW 语句.</returns>
    public static string ViewSql(string viewName, string dataTable, string resTable, string prefix)
    {
        if (prefix == LogsPrefix)
        {
            return $"CREATE VIEW IF NOT EXISTS {viewName} AS SELECT l.id, l.ts, l.service, l.severity, l.body, l.trace_id, l.span_id, l.attributes, r.resource, l.source FROM {dataTable} l LEFT JOIN {resTable} r ON l.resource_id = r.id";
        }

        return $"CREATE VIEW IF NOT EXISTS {viewName} AS SELECT l.trace_id, l.span_id, l.parent_span_id, l.ts, l.duration_us, l.service, l.name, l.kind, l.status, l.attributes, l.events, r.resource FROM {dataTable} l LEFT JOIN {resTable} r ON l.resource_id = r.id";
    }

    /// <summary>冻结批次表名：日期保留在末尾，以便复用日期解析.</summary>
    internal static string ArchiveName(string prefix, DateTimeOffset day) => ArchiveNames(prefix, day).View;

    /// <summary>
    /// 冻结批次三元组（同一 guid）：视图快照暴露宽列（归档 COPY/词项索引零改动），
    /// 伴生物理快照表不以 logs_/traces_ 开头，避免前缀枚举碰撞.
    /// </summary>
    /// <param name="prefix">天对象前缀.</param>
    /// <param name="day">天.</param>
    /// <returns>(视图快照名, 数据窄表快照名, 字典表快照名).</returns>
    internal static (string View, string Data, string Res) ArchiveNames(string prefix, DateTimeOffset day)
    {
        var guid = Guid.NewGuid().ToString("N");
        var dayString = day.UtcDateTime.ToString(DayFormat, CultureInfo.InvariantCulture);
        var view = $"{prefix}archive_{guid}_{dayString}";
        var dataPrefix = prefix == LogsPrefix ? LogDataPrefix : TraceDataPrefix;
        var resPrefix = prefix == LogsPrefix ? LogResPrefix : TraceResPrefix;
        return (view, $"{dataPrefix}archive_{guid}_{dayString}", $"{resPrefix}archive_{guid}_{dayString}");
    }

    /// <summary>
    /// 由视图快照名推导伴生物理快照表名（与 <see cref="ArchiveNames"/> 命名规则互逆）.
    /// </summary>
    /// <param name="prefix">天对象前缀.</param>
    /// <param name="snapshotView">视图快照名.</param>
    /// <returns>(数据窄表快照名, 字典表快照名).</returns>
    internal static (string Data, string Res) ArchiveCompanions(string prefix, string snapshotView)
    {
        var remainder = snapshotView[prefix.Length..]; // archive_{guid}_{day}
        var dataPrefix = prefix == LogsPrefix ? LogDataPrefix : TraceDataPrefix;
        var resPrefix = prefix == LogsPrefix ? LogResPrefix : TraceResPrefix;
        return (dataPrefix + remainder, resPrefix + remainder);
    }

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
    /// 生成建表 SQL（logs/traces 为 data 窄表 + resource 字典表 + 宽列视图三件套；
    /// 链路 trace_id 索引建在数据窄表上，可按配置省略）.
    /// </summary>
    /// <param name="tableName">天对象表名.</param>
    /// <param name="traceIndexEnabled">是否创建链路 trace_id ART 索引.</param>
    /// <returns>建表语句列表.</returns>
    public static IReadOnlyList<string> CreateSql(string tableName, bool traceIndexEnabled = true)
    {
        if (tableName.StartsWith(MqConsumptionPrefix, StringComparison.Ordinal))
        {
            return [$"CREATE TABLE IF NOT EXISTS {tableName} (ts TIMESTAMP NOT NULL, instance_id BIGINT NOT NULL, session_id VARCHAR NOT NULL, rule VARCHAR NOT NULL, match_mode VARCHAR NOT NULL, state VARCHAR NOT NULL, received_messages BIGINT, received_bytes BIGINT, persisted_records BIGINT, parse_failures BIGINT, skipped_messages BIGINT, confirmed_messages BIGINT, confirmation_failures BIGINT, sampling_gaps BIGINT, diagnostics VARCHAR)"];
        }

        if (tableName.StartsWith(LogsPrefix, StringComparison.Ordinal))
        {
            var day = ParseDay(tableName)!.Value;
            var dataTable = DataTableName(LogsPrefix, day);
            var resTable = ResTableName(LogsPrefix, day);
            return
            [
                $"CREATE TABLE IF NOT EXISTS {dataTable} (id BIGINT, ts TIMESTAMP NOT NULL, service VARCHAR NOT NULL, severity VARCHAR NOT NULL, body VARCHAR NOT NULL, trace_id VARCHAR, span_id VARCHAR, attributes VARCHAR, resource_id BIGINT, source VARCHAR)",
                $"CREATE TABLE IF NOT EXISTS {resTable} (id BIGINT NOT NULL, resource VARCHAR)",
                ViewSql(tableName, dataTable, resTable, LogsPrefix),
            ];
        }

        if (tableName.StartsWith(TracesPrefix, StringComparison.Ordinal))
        {
            var day = ParseDay(tableName)!.Value;
            var dataTable = DataTableName(TracesPrefix, day);
            var resTable = ResTableName(TracesPrefix, day);
            var sql = new List<string>
            {
                $"CREATE TABLE IF NOT EXISTS {dataTable} (trace_id VARCHAR NOT NULL, span_id VARCHAR NOT NULL, parent_span_id VARCHAR, ts TIMESTAMP NOT NULL, duration_us BIGINT NOT NULL, service VARCHAR NOT NULL, name VARCHAR NOT NULL, kind VARCHAR NOT NULL, status VARCHAR NOT NULL, attributes VARCHAR, events VARCHAR, resource_id BIGINT)",
                $"CREATE TABLE IF NOT EXISTS {resTable} (id BIGINT NOT NULL, resource VARCHAR)",
            };
            if (traceIndexEnabled)
            {
                sql.Add($"CREATE INDEX IF NOT EXISTS {dataTable}_tid_idx ON {dataTable} (trace_id)");
            }

            sql.Add(ViewSql(tableName, dataTable, resTable, TracesPrefix));
            return sql;
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
