using DuckDB.NET.Data;
using SData.Metrics.Promql;
using SData.Store;
using SData.Store.QueryDsl;

namespace SData.Metrics.Promql;

/// <summary>
/// PromQL AST → 参数化 SQL 计划（T711；表集合由 ITableResolver 提供，冷层经 read_parquet 合并）.
/// 语义约定：rate = (max-min)/range_seconds；即时选择器取区间末端值（arg_max）；
/// by(keys) 按 JSON 标签分组；without 降级为按序列全标签分组（已知限制，见 docs/metrics/bdd.md）.
/// </summary>
public static class PromqlSqlTranslator
{
    /// <summary>
    /// 由 AST 生成完整查询计划（调用层：Call→内层+rate 语义；Aggregate→内层+聚合层）.
    /// </summary>
    /// <param name="node">AST 根.</param>
    /// <param name="from">起始（含）.</param>
    /// <param name="to">结束（不含）.</param>
    /// <param name="tables">表集合.</param>
    /// <returns>计划；无数据源返回 null.</returns>
    public static PromqlSqlPlan? Build(PromqlNode node, DateTimeOffset from, DateTimeOffset to, ResolvedTables tables)
    {
        return node switch
        {
            PromqlVectorSelector selector => BuildSelector(selector, null, from, to, tables),
            PromqlCall call => call.Function switch
            {
                "rate" => BuildSelector(AsSelector(call.Argument), TimeSpan.FromSeconds(Math.Max(1, (long)(AsSelector(call.Argument).Range?.TotalSeconds ?? 0))), from, to, tables),
                _ => throw new PromqlParseException(call.Position, $"暂不支持的函数 {call.Function}"),
            },
            PromqlAggregate aggregate => BuildAggregate(aggregate, Build(aggregate.Argument, from, to, tables) ?? throw new PromqlParseException(aggregate.Position, "查询区间内无数据表")),
            PromqlNumber number => throw new PromqlParseException(number.Position, "独立数字查询暂不支持（作为聚合参数使用）"),
            _ => throw new PromqlParseException(0, "不支持的表达式"),
        };
    }

    /// <summary>
    /// 选择器（即时/范围）→ 每序列一行值.
    /// </summary>
    private static PromqlSqlPlan? BuildSelector(PromqlVectorSelector selector, TimeSpan? rateRange, DateTimeOffset from, DateTimeOffset to, ResolvedTables tables)
    {
        var sqlTables = BuildTableUnion(tables);
        if (sqlTables.Length == 0)
        {
            return null;
        }

        var parameters = new List<DuckDBParameter>
        {
            new("from", ToDbTime(from)),
            new("to", ToDbTime(to)),
        };
        var conditions = new List<string> { "ts >= $from", "ts < $to" };
        if (!string.IsNullOrEmpty(selector.MetricName))
        {
            parameters.Add(new DuckDBParameter("metric_name", selector.MetricName));
            conditions.Add("name = $metric_name");
        }

        for (var i = 0; i < selector.Matchers.Count; i++)
        {
            var matcher = selector.Matchers[i];
            parameters.Add(new DuckDBParameter($"label_path{i}", $"$.{matcher.Label}"));
            parameters.Add(new DuckDBParameter($"label_value{i}", matcher.Value));
            conditions.Add(matcher.Operator switch
            {
                "=" => $"json_extract_string(labels, $label_path{i}) = $label_value{i}",
                "!=" => $"(labels IS NULL OR json_extract_string(labels, $label_path{i}) != $label_value{i})",
                "=~" => $"regexp_matches(coalesce(json_extract_string(labels, $label_path{i}), ''), $label_value{i})",
                "!~" => $"(labels IS NULL OR NOT regexp_matches(coalesce(json_extract_string(labels, $label_path{i}), ''), $label_value{i}))",
                _ => throw new PromqlParseException(0, $"未知标签操作符 {matcher.Operator}"),
            });
        }

        string valueExpr;
        if (rateRange is { } range)
        {
            var seconds = Math.Max(1, (long)range.TotalSeconds);
            parameters.Add(new DuckDBParameter("range_seconds", seconds));
            valueExpr = "(max(value) - min(value)) / $range_seconds";
        }
        else
        {
            valueExpr = "arg_max(value, ts)";
        }

        var sql = $"""
            SELECT name, labels, {valueExpr} AS value
            FROM {sqlTables}
            WHERE {string.Join(" AND ", conditions)}
            GROUP BY name, labels
            """;
        return new PromqlSqlPlan(sql, parameters, tables);
    }

    /// <summary>
    /// 聚合层（sum/avg/min/max/count 单行；topk/bottomk 排序截断；by 分组）.
    /// </summary>
    private static PromqlSqlPlan BuildAggregate(PromqlAggregate aggregate, PromqlSqlPlan inner)
    {
        var parameters = new List<DuckDBParameter>(inner.Parameters);
        var op = aggregate.Operator;
        var hasGrouping = aggregate.GroupingLabels is { Count: > 0 };

        // 分组键（by）或序列键（without/无分组时按序列）
        var keys = hasGrouping && !aggregate.Without
            ? aggregate.GroupingLabels!.Select((label, i) =>
            {
                parameters.Add(new DuckDBParameter($"group_path{i}", $"$.{label}"));
                return $"json_extract_string(labels, $group_path{i})";
            }).ToArray()
            : ["labels"];
        var groupClause = $"GROUP BY {string.Join(", ", keys)}";

        string outerSql;
        if (op is "topk" or "bottomk")
        {
            parameters.Add(new DuckDBParameter("topn", aggregate.Param ?? 10));
            var direction = op == "topk" ? "DESC" : "ASC";
            var keyOut = hasGrouping && !aggregate.Without
                ? KeyProjections(aggregate.GroupingLabels!, keys)
                : "labels";
            outerSql = $"SELECT {keyOut}, value FROM ({inner.Sql}) ORDER BY value {direction} LIMIT $topn";
        }
        else
        {
            var func = op switch
            {
                "sum" => "sum(value)",
                "avg" => "avg(value)",
                "min" => "min(value)",
                "max" => "max(value)",
                "count" => "count(value)",
                _ => throw new PromqlParseException(aggregate.Position, $"未知聚合 {op}"),
            };
            // 无 by/without：单序列（labels 列恒 NULL）；by：分组键以原标签名输出（列名 = 标签名）
            var labelOut = hasGrouping && !aggregate.Without
                ? KeyProjections(aggregate.GroupingLabels!, keys)
                : "NULL AS labels";
            outerSql = $"SELECT {labelOut}, {func} AS value FROM ({inner.Sql}) {groupClause}";
        }

        return new PromqlSqlPlan(outerSql, parameters, inner.Tables);
    }

    /// <summary>
    /// DateTimeOffset → DuckDB TIMESTAMP（UTC）.
    /// </summary>
    private static DateTime ToDbTime(DateTimeOffset time)
    {
        return time.ToUniversalTime().UtcDateTime;
    }

    /// <summary>
    /// 分组键投影（json_extract 表达式 AS 原标签名，输出列名即标签名）.
    /// </summary>
    private static string KeyProjections(IReadOnlyList<string> labels, string[] keys)
    {
        return string.Join(", ", labels.Select((label, i) => $"{keys[i]} AS \"{label}\""));
    }

    /// <summary>
    /// 表集合 → FROM 子查询（本地 UNION ALL + 冷层 read_parquet；配置值单引号转义）.
    /// </summary>
    private static string BuildTableUnion(ResolvedTables tables)
    {
        var parts = new List<string>();
        if (tables.LocalTables.Count > 0)
        {
            parts.Add(string.Join(" UNION ALL ", tables.LocalTables.Select(t => $"SELECT name, labels, ts, value FROM {t}")));
        }

        foreach (var glob in tables.ColdParquetGlobs)
        {
            parts.Add($"SELECT name, labels, ts, value FROM read_parquet('{glob.Replace("'", "''")}', union_by_name=true)");
        }

        if (tables.ColdFiles.Count > 0)
        {
            // 台账驱动的逐日精确路径：列表形式合入单分支（与 Glob 兜底互斥）
            var files = string.Join(", ", tables.ColdFiles.Select(file => $"'{file.Replace("'", "''")}'"));
            parts.Add($"SELECT name, labels, ts, value FROM read_parquet([{files}], union_by_name=true)");
        }

        return parts.Count == 0 ? string.Empty : $"({string.Join(" UNION ALL ", parts)})";
    }

    private static PromqlVectorSelector AsSelector(PromqlNode node)
    {
        return node as PromqlVectorSelector
            ?? throw new PromqlParseException(node.Position, "rate 的参数必须是向量选择器（metric{labels}[range]）");
    }
}

/// <summary>
/// SQL 计划（Sql + 参数 + 表集合）.
/// </summary>
public sealed record PromqlSqlPlan(string Sql, IReadOnlyList<DuckDBParameter> Parameters, ResolvedTables Tables);
