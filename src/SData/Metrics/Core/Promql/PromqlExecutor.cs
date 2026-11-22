using DuckDB.NET.Data;
using SData.Infra.Exceptions;
using SData.Metrics.Promql;
using SData.Settings;
using SData.Store;
using SData.Store.Core;
using SData.Store.QueryDsl;

namespace SData.Metrics.Promql;

/// <summary>
/// PromQL 查询执行（T711）：解析 → 表解析（区间一次 ResolveAsync）→ 翻译 → 参数化执行 → Prometheus 形状序列.
/// 即时查询取区间末端；区间查询按 step 分桶对每桶执行聚合语义（共享同一份表解析结果）.
/// 跨度校验与 QueryDsl 同规则（MaxQueryDays；含冷层放宽至 ColdMaxQueryDays，均为运行参数）.
/// </summary>
public class PromqlExecutor : IPromqlExecutor
{
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly ITableResolver _tableResolver;
    private readonly ISystemSettingsProvider _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="PromqlExecutor"/> class.
    /// </summary>
    /// <param name="connectionManager">DuckDB 连接管理器.</param>
    /// <param name="tableResolver">表解析器（C2）.</param>
    /// <param name="settings">系统设置（跨度上限，保存即生效）.</param>
    public PromqlExecutor(DuckDbConnectionManager connectionManager, ITableResolver tableResolver, ISystemSettingsProvider settings)
    {
        _connectionManager = connectionManager;
        _tableResolver = tableResolver;
        _settings = settings;
    }

    /// <inheritdoc/>
    public async Task<PromqlResult> ExecuteAsync(string query, DateTimeOffset from, DateTimeOffset to, TimeSpan? step, CancellationToken cancellationToken = default)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var node = PromqlParser.Parse(query);

        // 区间一次表解析（全桶复用）；含冷层时放宽跨度上限（与 QueryDsl 同规则）
        var resolved = await _tableResolver.ResolveAsync(DataDomain.Metrics, from, to, cancellationToken: cancellationToken);
        var hasCold = resolved.ColdParquetGlobs.Count > 0 || resolved.ColdFiles.Count > 0;
        var limits = _settings.GetRuntime().DuckDb;
        var maxDays = Math.Max(1, hasCold
            ? Math.Max(limits.MaxQueryDays, limits.ColdMaxQueryDays)
            : limits.MaxQueryDays);
        if ((to - from).TotalDays > maxDays)
        {
            throw BusinessException.Throw(400, $"查询跨度 {(to - from).TotalDays:0.#} 天超过上限 {maxDays} 天");
        }

        var result = new PromqlResult
        {
            ResultType = step == null ? "vector" : "matrix",
        };

        if (step == null)
        {
            // 即时查询：区间整体求值，值点时间 = to - 1ms（区间末端）
            var plan = BuildPlan(node, from, to, resolved);
            if (plan != null)
            {
                var series = await ReadSeriesAsync(plan, cancellationToken);
                var stamp = to.AddMilliseconds(-1);
                foreach (var (metric, value) in series)
                {
                    result.Result.Add(new PromqlSeries { Metric = metric, Value = (stamp, value) });
                }
            }

            return result;
        }

        // 区间查询：按步长分桶，共享区间级表解析；rate 类表达式按声明窗口向前回看
        // （Prometheus 语义：评估点 t 取 (t-range, t] 的样本，窗口可跨越 step 桶），其余表达式按桶窗口
        var safeStep = step.Value <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : step.Value;
        var rateLookback = RateLookback(node);
        for (var bucket = from; bucket < to; bucket = bucket.Add(safeStep))
        {
            var bucketEnd = bucket.Add(safeStep) > to ? to : bucket.Add(safeStep);
            var dataFrom = bucket;
            if (rateLookback is { } lookback)
            {
                // rate 数据窗口 = (bucketEnd - lookback, bucketEnd]，前端点仍在 bucketEnd；越出表解析起点时钳制到 from
                dataFrom = bucketEnd - lookback;
                if (dataFrom < from)
                {
                    dataFrom = from;
                }
            }

            var bucketPlan = BuildPlan(node, dataFrom, bucketEnd, resolved);
            if (bucketPlan == null)
            {
                continue;
            }

            var bucketSeries = await ReadSeriesAsync(bucketPlan, cancellationToken);
            var stamp = bucketEnd.AddMilliseconds(-1);
            foreach (var (metric, value) in bucketSeries)
            {
                var existing = result.Result.FirstOrDefault(x => DictionaryEquals(x.Metric, metric));
                if (existing == null)
                {
                    existing = new PromqlSeries { Metric = metric };
                    result.Result.Add(existing);
                }

                existing.Values.Add((stamp, value));
            }
        }

        return result;
    }

    private static PromqlSqlPlan? BuildPlan(PromqlNode node, DateTimeOffset from, DateTimeOffset to, ResolvedTables tables)
    {
        return PromqlSqlTranslator.Build(node, from, to, tables);
    }

    /// <summary>
    /// 提取表达式中 rate 声明的回看窗口（如 rate(x[5m]) → 5m）；无 rate 返回 null.
    /// </summary>
    private static TimeSpan? RateLookback(PromqlNode node)
    {
        return node switch
        {
            PromqlCall { Function: "rate", Argument: PromqlVectorSelector { Range: { } range } } => range,
            PromqlCall call => RateLookback(call.Argument),
            PromqlAggregate aggregate => RateLookback(aggregate.Argument),
            _ => null,
        };
    }

    private async Task<List<(Dictionary<string, string> Metric, double Value)>> ReadSeriesAsync(PromqlSqlPlan plan, CancellationToken cancellationToken)
    {
        var series = new List<(Dictionary<string, string>, double)>();
        await _connectionManager.QueryAsync(plan.Sql, [.. plan.Parameters], async reader =>
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var metric = new Dictionary<string, string>();

                // 通用列映射：末列为 value，其余按列名处理（name→__name__，labels→JSON 展开，其余列名即标签名）
                for (var i = 0; i < reader.FieldCount - 1; i++)
                {
                    if (reader.IsDBNull(i))
                    {
                        continue;
                    }

                    var key = reader.GetName(i);
                    var raw = reader.GetValue(i);
                    if (key == "labels" && raw is string labelsJson)
                    {
                        MergeLabels(metric, labelsJson);
                    }
                    else if (raw is string labelValue)
                    {
                        metric[key == "name" ? "__name__" : key] = labelValue;
                    }
                }

                var value = Convert.ToDouble(reader.GetValue(reader.FieldCount - 1));
                series.Add((metric, value));
            }

            return 0;
        }, cancellationToken);
        return series;
    }

    private static void MergeLabels(Dictionary<string, string> metric, string labelsJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(labelsJson);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    metric[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 非法 labels JSON 忽略标签内容，保留其余列
        }
    }

    private static bool DictionaryEquals(Dictionary<string, string> left, Dictionary<string, string> right)
    {
        return left.Count == right.Count && !left.Except(right).Any();
    }
}
