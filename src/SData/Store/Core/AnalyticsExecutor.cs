using System.Text.Json;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using SData.Infra.Configuration;
using SData.Infra.Exceptions;
using SData.Store.Models;
using SData.Store.QueryDsl;

namespace SData.Store;

/// <summary>
/// DuckDB 只读分析查询执行器.
/// 安全约束：仅允许单条 SELECT/WITH 语句；拒绝任何写操作关键字；强制行数上限；外部输入一律走参数绑定.
/// </summary>
public partial class AnalyticsExecutor : IAnalyticsExecutor
{
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly SystemOptions _systemOptions;
    private readonly ITableResolver _tableResolver;
    private readonly ILogger<AnalyticsExecutor> _logger;
    private readonly ColdLedgerCache? _alertLedger;
    private readonly SData.Settings.ISystemSettingsProvider _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalyticsExecutor"/> class.
    /// </summary>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="systemOptions">系统配置（旧版 DuckDb.Retention 回退）.</param>
    /// <param name="settings">系统设置（查询跨度与超时、保留计划，保存即生效）.</param>
    /// <param name="tableResolver">表解析器（统一查询管线，ExecuteSearchAsync 冷层来源）.</param>
    /// <param name="logger">日志.</param>
    /// <param name="alertLedger">告警用冷台账（可空）.</param>
    public AnalyticsExecutor(DuckDbConnectionManager connectionManager, SystemOptions systemOptions, SData.Settings.ISystemSettingsProvider settings, ITableResolver tableResolver, ILogger<AnalyticsExecutor> logger, ColdLedgerCache? alertLedger = null)
    {
        _connectionManager = connectionManager;
        _systemOptions = systemOptions;
        _settings = settings;
        _tableResolver = tableResolver;
        _logger = logger;
        _alertLedger = alertLedger;
    }

    /// <inheritdoc/>
    public async Task<AlertCountResult> ExecuteAlertCountAsync(AlertCountQuery query, CancellationToken cancellationToken = default)
    {
        var compiled = AlertConditionCompiler.Compile(query.Domain, query.Condition, query.Parameters);
        if (query.From >= query.To || query.To - query.From > TimeSpan.FromHours(1) || query.Cap is < 1 or > 1_000_001)
        {
            throw BusinessException.Throw(400, "告警窗口或计数上限不合法");
        }

        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var domain = query.Domain == "logs" ? DataDomain.Logs : DataDomain.Traces;
        var (hotDays, _) = ArchiveService.ResolveRetention(domain, _settings.GetRetention(), _systemOptions.DuckDb);
        if (hotDays <= 0 || query.From.UtcDateTime.Date < DateTime.UtcNow.Date.AddDays(-hotDays))
        {
            throw BusinessException.Throw(400, "告警窗口超出本地热数据覆盖范围");
        }

        if (_alertLedger != null && (await _alertLedger.GetAsync(query.Domain, cancellationToken))
            .Any(entry => entry.Day < query.To && entry.Day.AddDays(1) > query.From))
        {
            throw BusinessException.Throw(400, "告警窗口含已归档数据，不能仅按本地数据评估");
        }

        var prefix = query.Domain == "logs" ? TimeTables.LogsPrefix : TimeTables.TracesPrefix;
        var expanded = SearchSqlExpander.Expand(
            $"SELECT count(*) FROM (SELECT 1 FROM {{{{{query.Domain}}}}} WHERE ts >= $start AND ts < $end AND ({compiled.Sql}) LIMIT CAST($alert_cap AS BIGINT)) alert_matches",
            _ => new ResolvedTables(_connectionManager.GetDayTables(prefix, query.From, query.To), Array.Empty<string>()));
        if (expanded == null)
        {
            return new AlertCountResult(0, false);
        }

        using var alertQueryLease = await _connectionManager.LockAlertQueryAsync(cancellationToken);
        // DuckDB 的异步 API 底层同步执行。计时器持续中断专属连接，覆盖取消先于执行的竞争窗口。
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = _connectionManager.CloneConnection();
            using var command = connection.CreateCommand();
            command.CommandText = expanded.Sql;
            command.Parameters.Add(new DuckDBParameter("start", ToUtc(query.From)));
            command.Parameters.Add(new DuckDBParameter("end", ToUtc(query.To)));
            command.Parameters.Add(new DuckDBParameter("alert_cap", query.Cap));
            foreach (var parameter in compiled.Parameters)
            {
                command.Parameters.Add(new DuckDBParameter(parameter.Key, ToParameterValue(parameter.Key, parameter.Value)));
            }

            using var timer = new Timer(_ =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    command.Cancel();
                }
            }, null, 50, 50);
            try
            {
                var count = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                cancellationToken.ThrowIfCancellationRequested();
                return new AlertCountResult(count, count >= query.Cap);
            }
            finally
            {
                // 等待回调退出后才允许 command/connection 释放。
                timer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }, CancellationToken.None);
    }

    /// <inheritdoc/>
    public async Task<AnalyticsQueryResult> ExecuteAsync(string sql, int rowLimit = 1000, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        Validate(sql);
        return await ExecuteCoreAsync(sql, parameters: null, rowLimit, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<AnalyticsQueryResult> ExecuteSearchAsync(string sql, DateTimeOffset from, DateTimeOffset to, int rowLimit = 500, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL is empty.");
        }

        if (from == default || to == default)
        {
            throw BusinessException.Throw(400, "缺少时间范围（from/to）");
        }

        if (from >= to)
        {
            throw BusinessException.Throw(400, "时间范围 from 必须早于 to");
        }

        // 预扫描引用的占位符域并解析（统一查询管线：本地天表 + 冷层；内存台账镜像，每域 <1ms）
        var resolvedByPrefix = new Dictionary<string, ResolvedTables>(StringComparer.Ordinal);
        foreach (var (token, prefix, domain) in new[]
                 {
                     ("logs", TimeTables.LogsPrefix, SData.Store.QueryDsl.DataDomain.Logs),
                     ("traces", TimeTables.TracesPrefix, SData.Store.QueryDsl.DataDomain.Traces),
                     ("metrics", TimeTables.MetricsPrefix, SData.Store.QueryDsl.DataDomain.Metrics),
                     ("iot", TimeTables.IotPrefix, SData.Store.QueryDsl.DataDomain.Iot),
                     ("sessions", TimeTables.RumSessionPrefix, SData.Store.QueryDsl.DataDomain.RumSession),
                     ("rum_events", TimeTables.RumEventPrefix, SData.Store.QueryDsl.DataDomain.RumEvent),
                 })
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(sql, $@"\{{{{\s*{token}\s*\}}}}", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                resolvedByPrefix[prefix] = await _tableResolver.ResolveAsync(domain, from, to, cancellationToken: cancellationToken);
            }
        }

        // 跨度校验：含冷层时放宽至 ColdMaxQueryDays（与 QueryDsl 同规则）
        var hasCold = resolvedByPrefix.Values.Any(r => r.ColdParquetGlobs.Count > 0 || r.ColdFiles.Count > 0);
        var limits = _settings.GetRuntime().DuckDb;
        var maxDays = Math.Max(1, hasCold
            ? Math.Max(limits.MaxQueryDays, limits.ColdMaxQueryDays)
            : limits.MaxQueryDays);
        var spanDays = (to - from).TotalDays;
        if (spanDays > maxDays)
        {
            throw BusinessException.Throw(400, hasCold
                ? $"查询跨度 {spanDays:0.#} 天超过上限 {maxDays} 天（ColdMaxQueryDays）"
                : $"查询跨度 {spanDays:0.#} 天超过上限 {maxDays} 天（MaxQueryDays）");
        }

        // 表占位符展开：表名只来自 TimeTables 前缀 + 台账对象键（非用户输入）；区间内无数据源时短路空结果
        var expanded = SearchSqlExpander.Expand(sql.Trim(), prefix =>
            resolvedByPrefix.TryGetValue(prefix, out var resolved)
                ? resolved
                : new SData.Store.QueryDsl.ResolvedTables(_connectionManager.GetDayTables(prefix, from, to), Array.Empty<string>()));
        if (expanded == null)
        {
            return new AnalyticsQueryResult();
        }

        Validate(expanded.Sql);

        var bound = new List<DuckDBParameter>();
        if (expanded.UsesStart)
        {
            bound.Add(new DuckDBParameter("start", ToUtc(from)));
        }

        if (expanded.UsesEnd)
        {
            bound.Add(new DuckDBParameter("end", ToUtc(to)));
        }

        // 其余 $name 引用：一律以外部值参数绑定（缺失即拒绝，杜绝字符串拼接）
        foreach (var name in expanded.ReferencedParams)
        {
            if (name is "start" or "end")
            {
                continue;
            }

            if (parameters == null || !parameters.TryGetValue(name, out var value))
            {
                throw BusinessException.Throw(400, $"缺少查询参数 ${name}");
            }

            bound.Add(new DuckDBParameter(name, ToParameterValue(name, value)));
        }

        return await ExecuteCoreAsync(expanded.Sql, bound, rowLimit, cancellationToken);
    }

    /// <summary>
    /// 执行核心（行数上限 + 逐行归一化读取）.
    /// </summary>
    /// <param name="sql">已校验的 SQL.</param>
    /// <param name="parameters">绑定参数（可空）.</param>
    /// <param name="rowLimit">行数上限.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>结果.</returns>
    private async Task<AnalyticsQueryResult> ExecuteCoreAsync(string sql, IReadOnlyList<DuckDBParameter>? parameters, int rowLimit, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_settings.GetRuntime().DuckDb.QueryTimeoutSeconds, 1, 300)));
        try
        {
            return await ExecuteBoundedAsync(sql, parameters, rowLimit, budget.Token);
        }
        catch (Exception exception) when (budget.IsCancellationRequested && exception is OperationCanceledException or DuckDBException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning("Analytics query exceeded execution budget");
            throw BusinessException.Throw(408, "分析查询超时，请缩小时间范围或简化查询");
        }
        catch (DuckDBException exception)
        {
            _connectionManager.LogResourceFailure(exception, "analytics");
            throw;
        }
    }

    private async Task<AnalyticsQueryResult> ExecuteBoundedAsync(string sql, IReadOnlyList<DuckDBParameter>? parameters, int rowLimit, CancellationToken cancellationToken)
    {
        rowLimit = Math.Clamp(rowLimit, 1, 10_000);
        using var queryLease = await _connectionManager.LockQueryAsync(cancellationToken);

        // 驱动的异步调用底层同步执行；独立工作线程和周期 Cancel 确保截止时间中断原生查询。
        return await Task.Run(async () =>
        {
        cancellationToken.ThrowIfCancellationRequested();
        var started = DateTimeOffset.UtcNow;
        await using var connection = _connectionManager.CloneConnection();
        await using var command = ((DuckDBConnection)connection).CreateCommand();
        var limitName = "sdata_result_limit";
        while (sql.Contains("$" + limitName, StringComparison.OrdinalIgnoreCase)
            || parameters?.Any(parameter => string.Equals(parameter.ParameterName.TrimStart('$'), limitName, StringComparison.OrdinalIgnoreCase)) == true)
        {
            limitName += "_";
        }

        command.CommandText = $"SELECT * FROM (\n{sql}\n) AS sdata_bounded_result LIMIT CAST(${limitName} AS BIGINT)";
        command.UseStreamingMode = true;
        command.Parameters.Add(new DuckDBParameter(limitName, rowLimit + 1));
        if (parameters != null)
        {
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(parameter);
            }
        }

        await using var timer = new Timer(_ =>
        {
            if (cancellationToken.IsCancellationRequested) command.Cancel();
        }, null, 50, 50);
        var result = new AnalyticsQueryResult();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                result.Columns.Add(reader.GetName(i));
            }

            while (await reader.ReadAsync(cancellationToken))
            {
                if (result.Rows.Count >= rowLimit)
                {
                    result.Truncated = true;
                    break;
                }

                var row = new List<object?>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row.Add(Normalize(reader.GetValue(i)));
                }

                result.Rows.Add(row);
            }
        }
        catch (DuckDBException ex) when (ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            // 按天分表在当天首次写入时才创建：查询不存在的日期分表属于用户可修正的输入问题，转业务错误而非 500
            throw BusinessException.Throw(400, "查询的数据表不存在（按天分表在当天首次写入数据后才会创建），请确认表名与日期");
        }

        cancellationToken.ThrowIfCancellationRequested();
        result.ElapsedMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
        return result;
        }, CancellationToken.None);
    }

    /// <summary>
    /// 时间转 DuckDB 参数值（UTC DateTime，与 QueryDslCompiler.ToDb 同口径）.
    /// </summary>
    /// <param name="time">时间.</param>
    /// <returns>UTC 时间.</returns>
    private static DateTime ToUtc(DateTimeOffset time)
    {
        return time.ToUniversalTime().UtcDateTime;
    }

    /// <summary>
    /// 命名参数值转 DuckDB 参数值：JSON 绑定的 JsonElement 归一为 string/long/double/bool/DBNull 基元.
    /// </summary>
    /// <param name="name">参数名（用于错误信息）.</param>
    /// <param name="value">外部值（JsonElement 或基元类型）.</param>
    /// <returns>DuckDB 可绑定的值.</returns>
    private static object ToParameterValue(string name, object? value)
    {
        if (value == null)
        {
            return DBNull.Value;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? (object)DBNull.Value,
                JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => DBNull.Value,
                _ => throw BusinessException.Throw(400, $"查询参数 ${name} 的值类型不支持（仅允许字符串/数字/布尔/null）"),
            };
        }

        // 直接调用方（非 JSON 反序列化）传基元类型时原样接受
        if (value is string or bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            return value;
        }

        throw BusinessException.Throw(400, $"查询参数 ${name} 的值类型不支持（仅允许字符串/数字/布尔/null）");
    }

    private static void Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL is empty.");
        }

        var trimmed = sql.Trim();
        if (trimmed.Contains(';'))
        {
            throw new ArgumentException("Multiple statements are not allowed.");
        }

        if (!Regex.IsMatch(trimmed, @"^(select|with)\b", RegexOptions.IgnoreCase))
        {
            throw new ArgumentException("Only SELECT/WITH queries are allowed.");
        }

        if (ForbiddenKeyword().IsMatch(trimmed))
        {
            throw new ArgumentException("Write/admin statements are not allowed.");
        }
    }

    private static object? Normalize(object? value)
    {
        if (value == null || value == DBNull.Value)
        {
            return null;
        }

        return value switch
        {
            System.Numerics.BigInteger bigInt => bigInt.ToString(),
            DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            _ => value,
        };
    }

    [GeneratedRegex(@"\b(insert|update|delete|create|drop|alter|attach|detach|copy|export|import|install|load|pragma|set|reset|vacuum|checkpoint|call|grant|revoke|analyze|comment|secret)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenKeyword();
}
