using System.Data.Common;
using System.Globalization;
using DuckDB.NET.Data;
using SData.Infra.Configuration;
using SData.Infra.Exceptions;
using SData.Store.Models;
using SData.Store.QueryDsl;

namespace SData.Store;

/// <summary>
/// 统一查询 DSL 执行器（IQueryDslExecutor 实现）：编译 → 现有天表过滤 → 执行 → 列式响应.
/// 含 MaxQueryDays 跨度约束；区间内无任何天表时短路返回空响应.
/// </summary>
public class QueryDslExecutorService : IQueryDslExecutor
{
    private static readonly Dictionary<DataDomain, string[]> RawColumns = new()
    {
        [DataDomain.Logs] = ["id", "ts", "service", "severity", "body", "trace_id", "span_id", "attributes", "resource", "source"],
        [DataDomain.Traces] = ["trace_id", "span_id", "parent_span_id", "ts", "duration_us", "service", "name", "kind", "status", "attributes", "events", "resource"],
        [DataDomain.Metrics] = ["name", "ts", "value", "labels", "kind"],
        [DataDomain.Iot] = ["device_key", "signal", "ts", "num_value", "str_value", "quality"],
    };

    private readonly DuckDbConnectionManager _connectionManager;
    private readonly SystemOptions _systemOptions;
    private readonly ITableResolver _tableResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryDslExecutorService"/> class.
    /// </summary>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="systemOptions">系统配置（MaxQueryDays）.</param>
    /// <param name="tableResolver">表解析器（契约 C2）.</param>
    public QueryDslExecutorService(DuckDbConnectionManager connectionManager, SystemOptions systemOptions, ITableResolver tableResolver)
    {
        _connectionManager = connectionManager;
        _systemOptions = systemOptions;
        _tableResolver = tableResolver;
    }

    /// <inheritdoc/>
    public async Task<SearchDslResponse> ExecuteAsync(QueryDslModel model, DataDomain domain, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(model);
        if (model.Range == null)
        {
            throw BusinessException.Throw(400, "缺少 range 时间范围（from/to）");
        }

        var resolved = await _tableResolver.ResolveAsync(domain, model.Range.From, model.Range.To, ExtractSearchKeyword(domain, model), cancellationToken);

        // 含冷层（归档对象）的查询放宽跨度上限：ColdMaxQueryDays 与 MaxQueryDays 取较大值
        var hasCold = resolved.ColdParquetGlobs.Count > 0 || resolved.ColdFiles.Count > 0;
        var maxDays = Math.Max(1, hasCold
            ? Math.Max(_systemOptions.DuckDb.MaxQueryDays, _systemOptions.DuckDb.ColdMaxQueryDays)
            : _systemOptions.DuckDb.MaxQueryDays);
        var spanDays = (model.Range.To - model.Range.From).TotalDays;
        if (spanDays > maxDays)
        {
            throw BusinessException.Throw(400, hasCold
                ? $"查询跨度 {spanDays:0.#} 天超过上限 {maxDays} 天（ColdMaxQueryDays）"
                : $"查询跨度 {spanDays:0.#} 天超过上限 {maxDays} 天（MaxQueryDays）");
        }

        if (resolved.LocalTables.Count == 0 && resolved.ColdParquetGlobs.Count == 0 && resolved.ColdFiles.Count == 0)
        {
            return new SearchDslResponse
            {
                Columns = [.. RawColumns[domain]],
                TablesScanned = 0,
            };
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var compiled = QueryDslCompiler.Compile(model, domain, resolved);
        var response = await _connectionManager.QueryAsync(compiled.Sql, compiled.Parameters, async reader =>
        {
            var result = new SearchDslResponse();
            var fieldCount = reader.FieldCount;
            for (var i = 0; i < fieldCount; i++)
            {
                result.Columns.Add(reader.GetName(i));
            }

            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < fieldCount; i++)
                {
                    row[reader.GetName(i)] = ConvertValue(reader.GetValue(i));
                }

                result.Rows.Add(row);
            }

            return result;
        }, cancellationToken);
        response.TablesScanned = resolved.LocalTables.Count;
        response.Took = stopwatch.ElapsedMilliseconds;
        return response;
    }

    /// <summary>
    /// 提取全文检索关键词（logs 域 body 的 match 过滤；多个 match 过滤 AND 语义 = 词项并集）.
    /// </summary>
    /// <param name="domain">数据域.</param>
    /// <param name="model">DSL 模型.</param>
    /// <returns>关键词或 null.</returns>
    private static string? ExtractSearchKeyword(DataDomain domain, QueryDslModel model)
    {
        if (domain != DataDomain.Logs)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var filter in model.Filters)
        {
            if (filter.Field == "body"
                && string.Equals(filter.Op, "match", StringComparison.OrdinalIgnoreCase)
                && filter.Value is System.Text.Json.JsonElement element
                && element.ValueKind == System.Text.Json.JsonValueKind.String
                && element.GetString() is { } value
                && !string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value);
            }
        }

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    private static object? ConvertValue(object? value)
    {
        return value switch
        {
            null => null,
            DateTime dateTime => dateTime.Kind == DateTimeKind.Utc ? new DateTimeOffset(dateTime, TimeSpan.Zero) : new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc), TimeSpan.Zero),
            short or int or long => Convert.ToInt64(value),
            float or double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToHexString(bytes),
            _ => value,
        };
    }
}
