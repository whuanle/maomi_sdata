using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using SData.Infra.Exceptions;

namespace SData.Store.QueryDsl;

/// <summary>
/// 编译产物（契约 C1）：SQL 全部外部值参数绑定，表名仅来自 TimeTables（内部日期，非用户输入）.
/// </summary>
/// <param name="Sql">可执行 SELECT 语句.</param>
/// <param name="Parameters">绑定参数.</param>
/// <param name="Tables">涉及的本地天表集合（T510 冷层扩展时含 read_parquet 分支）.</param>
public sealed record CompiledQuery(string Sql, IReadOnlyList<DuckDBParameter> Parameters, IReadOnlyList<string> Tables);

/// <summary>
/// 统一查询 DSL 编译器（契约 C1）：把 QueryDslModel 编译为 DuckDB 参数化 SELECT.
/// 语义：filters AND 串联；聚合模式（bucket/aggregations/groupBy 至少其一）输出别名供排序引用；
/// 原始行模式输出全列；字段与操作符白名单外一律拒绝（BusinessException，StatusCode=400）；
/// JSON 路径字段（attributes./resource./labels.）经 json_extract_string 提取，聚合按数值解释（TRY_CAST，非数值行不参与）.
/// </summary>
public static class QueryDslCompiler
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 10000;
    private const int SeedParameterCount = 2;

    private const string OpEq = "eq";
    private const string OpNeq = "neq";
    private const string OpIn = "in";
    private const string OpPrefix = "prefix";
    private const string OpMatch = "match";
    private const string OpGt = "gt";
    private const string OpGte = "gte";
    private const string OpLt = "lt";
    private const string OpLte = "lte";

    private static readonly HashSet<string> AllowedOps = new(StringComparer.Ordinal) { OpEq, OpNeq, OpIn, OpPrefix, OpMatch, OpGt, OpGte, OpLt, OpLte };

    private static readonly HashSet<string> ComparisonOps = new(StringComparer.Ordinal) { OpGt, OpGte, OpLt, OpLte };

    private static readonly HashSet<string> AllowedAggFuncs = new(StringComparer.Ordinal) { "count", "avg", "sum", "min", "max", "quantile" };

    private enum FieldKind
    {
        String,
        Number,
        Long,
        Time,
        Json,
    }

    private sealed record FieldSpec(string Expression, FieldKind Kind, bool IsJsonPath);

    private static readonly Dictionary<DataDomain, Dictionary<string, FieldSpec>> Schemas = new()
    {
        [DataDomain.Logs] = new(StringComparer.Ordinal)
        {
            ["id"] = new FieldSpec("id", FieldKind.Long, false),
            ["ts"] = new FieldSpec("ts", FieldKind.Time, false),
            ["service"] = new FieldSpec("service", FieldKind.String, false),
            ["severity"] = new FieldSpec("severity", FieldKind.String, false),
            ["body"] = new FieldSpec("body", FieldKind.String, false),
            ["trace_id"] = new FieldSpec("trace_id", FieldKind.String, false),
            ["span_id"] = new FieldSpec("span_id", FieldKind.String, false),
            ["attributes"] = new FieldSpec("attributes", FieldKind.Json, false),
            ["resource"] = new FieldSpec("resource", FieldKind.Json, false),
            ["source"] = new FieldSpec("source", FieldKind.String, false),
        },
        [DataDomain.Traces] = new(StringComparer.Ordinal)
        {
            ["trace_id"] = new FieldSpec("trace_id", FieldKind.String, false),
            ["span_id"] = new FieldSpec("span_id", FieldKind.String, false),
            ["parent_span_id"] = new FieldSpec("parent_span_id", FieldKind.String, false),
            ["ts"] = new FieldSpec("ts", FieldKind.Time, false),
            ["duration_us"] = new FieldSpec("duration_us", FieldKind.Number, false),
            ["service"] = new FieldSpec("service", FieldKind.String, false),
            ["name"] = new FieldSpec("name", FieldKind.String, false),
            ["kind"] = new FieldSpec("kind", FieldKind.String, false),
            ["status"] = new FieldSpec("status", FieldKind.String, false),
            ["attributes"] = new FieldSpec("attributes", FieldKind.Json, false),
            ["events"] = new FieldSpec("events", FieldKind.Json, false),
            ["resource"] = new FieldSpec("resource", FieldKind.Json, false),
        },
        [DataDomain.Metrics] = new(StringComparer.Ordinal)
        {
            ["name"] = new FieldSpec("name", FieldKind.String, false),
            ["ts"] = new FieldSpec("ts", FieldKind.Time, false),
            ["value"] = new FieldSpec("value", FieldKind.Number, false),
            ["labels"] = new FieldSpec("labels", FieldKind.Json, false),
            ["kind"] = new FieldSpec("kind", FieldKind.String, false),
        },
        [DataDomain.Iot] = new(StringComparer.Ordinal)
        {
            ["device_key"] = new FieldSpec("device_key", FieldKind.String, false),
            ["signal"] = new FieldSpec("signal", FieldKind.String, false),
            ["ts"] = new FieldSpec("ts", FieldKind.Time, false),
            ["num_value"] = new FieldSpec("num_value", FieldKind.Number, false),
            ["str_value"] = new FieldSpec("str_value", FieldKind.String, false),
            ["quality"] = new FieldSpec("quality", FieldKind.Number, false),
        },
    };

    private static readonly Dictionary<DataDomain, string> Projections = new()
    {
        [DataDomain.Logs] = "id, ts, service, severity, body, trace_id, span_id, attributes, resource, source",
        [DataDomain.Traces] = "trace_id, span_id, parent_span_id, ts, duration_us, service, name, kind, status, attributes, events, resource",
        [DataDomain.Metrics] = "name, ts, value, labels, kind",
        [DataDomain.Iot] = "device_key, signal, ts, num_value, str_value, quality",
    };

    private static readonly Dictionary<DataDomain, string> Prefixes = new()
    {
        [DataDomain.Logs] = TimeTables.LogsPrefix,
        [DataDomain.Traces] = TimeTables.TracesPrefix,
        [DataDomain.Metrics] = TimeTables.MetricsPrefix,
        [DataDomain.Iot] = TimeTables.IotPrefix,
    };

    /// <summary>
    /// 编译查询（契约 C1 入口）.
    /// </summary>
    /// <param name="model">DSL 请求模型.</param>
    /// <param name="domain">数据域.</param>
    /// <param name="tables">表解析覆盖（契约 C2，由调用方经 ITableResolver 提供；缺省按 range 展开本地天表名）.</param>
    /// <returns>编译产物.</returns>
    public static CompiledQuery Compile(QueryDslModel model, DataDomain domain, ResolvedTables? tables = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!Schemas.TryGetValue(domain, out var schema))
        {
            throw BusinessException.Throw(400, $"未知查询域：{domain}");
        }

        var range = ValidateRange(model.Range);
        var resolved = tables ?? new ResolvedTables(DayTables(domain, range), Array.Empty<string>());

        var parameters = new List<DuckDBParameter>
        {
            new("start", ToDb(range.From)),
            new("end", ToDb(range.To)),
        };
        var where = BuildFilters(model.Filters, domain, schema, parameters);

        var aggregateMode = model.Bucket != null || model.Aggregations.Count > 0 || model.GroupBy.Count > 0;
        var sql = aggregateMode
            ? BuildAggregateQuery(model, domain, schema, resolved, where, parameters)
            : BuildRawQuery(model, domain, resolved, where, parameters);
        return new CompiledQuery(sql, parameters, resolved.LocalTables);
    }

    private static QueryRange ValidateRange(QueryRange? range)
    {
        if (range == null)
        {
            throw BusinessException.Throw(400, "缺少 range 时间范围（from/to）");
        }

        if (range.From == default || range.To == default)
        {
            throw BusinessException.Throw(400, "range.from 与 range.to 均为必填");
        }

        if (range.From >= range.To)
        {
            throw BusinessException.Throw(400, "range.from 必须早于 range.to");
        }

        return range;
    }

    private static List<string> DayTables(DataDomain domain, QueryRange range)
    {
        var prefix = Prefixes[domain];
        var tables = new List<string>();
        var day = range.From.UtcDateTime.Date;
        var last = range.To.UtcDateTime.Date;
        while (day <= last)
        {
            tables.Add(TimeTables.Name(prefix, new DateTimeOffset(day, TimeSpan.Zero)));
            day = day.AddDays(1);
        }

        return tables;
    }

    private static string BuildRawQuery(QueryDslModel model, DataDomain domain, ResolvedTables resolved, List<string> where, List<DuckDBParameter> parameters)
    {
        var order = BuildRawOrderBy(model.Sort, domain, Schemas[domain]);
        ResolveLimit(model.Limit, parameters);
        return $"SELECT {Projections[domain]} FROM ({Union(resolved, domain)}){WhereClause(where)}{order} LIMIT $limit";
    }

    private static string BuildAggregateQuery(QueryDslModel model, DataDomain domain, Dictionary<string, FieldSpec> schema, ResolvedTables resolved, List<string> where, List<DuckDBParameter> parameters)
    {
        if (model.Bucket == null && model.Aggregations.Count == 0)
        {
            throw BusinessException.Throw(400, "groupBy 需与 aggregations 或 bucket 至少其一搭配");
        }

        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var select = new List<string>();
        var groupBy = new List<string>();

        if (model.Bucket != null)
        {
            var interval = ParseStep(model.Bucket.Step);
            select.Add($"time_bucket({interval}, ts) AS \"bucket\"");
            groupBy.Add("\"bucket\"");
            aliases.Add("bucket");
        }

        foreach (var field in model.GroupBy)
        {
            var alias = QuoteAlias(field, allowDot: true);
            if (!aliases.Add(alias))
            {
                throw BusinessException.Throw(400, $"输出列别名重复：{field}");
            }

            var spec = ResolveField(field, domain, schema);
            select.Add($"{spec.Expression} AS {alias}");
            groupBy.Add(alias);
        }

        var aggregations = model.Aggregations;
        if (aggregations.Count == 0 && model.Bucket != null)
        {
            // 仅分桶时用 bucket.func 作为唯一聚合
            aggregations = [new QueryAggregation { Func = model.Bucket.Func, Field = model.Bucket.Field, Q = model.Bucket.Q, Alias = model.Bucket.Func }];
        }

        foreach (var aggregation in aggregations)
        {
            select.Add(BuildAggregateExpression(aggregation, domain, schema, parameters, aliases));
        }

        var order = BuildAggregateOrderBy(model.Sort, aliases, hasBucket: model.Bucket != null);
        ResolveLimit(model.Limit, parameters);
        return $"SELECT {string.Join(", ", select)} FROM ({Union(resolved, domain)}){WhereClause(where)} GROUP BY {string.Join(", ", groupBy)}{order} LIMIT $limit";
    }

    private static string BuildAggregateExpression(QueryAggregation aggregation, DataDomain domain, Dictionary<string, FieldSpec> schema, List<DuckDBParameter> parameters, HashSet<string> aliases)
    {
        var func = aggregation.Func?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!AllowedAggFuncs.Contains(func))
        {
            throw BusinessException.Throw(400, $"不支持的聚合函数：{aggregation.Func}（白名单 count/avg/sum/min/max/quantile）");
        }

        var alias = QuoteAlias(string.IsNullOrWhiteSpace(aggregation.Alias) ? func : aggregation.Alias, allowDot: true);
        if (!aliases.Add(alias))
        {
            throw BusinessException.Throw(400, $"输出列别名重复：{alias}");
        }

        if (func == "count")
        {
            return $"COUNT(*) AS {alias}";
        }

        if (string.IsNullOrWhiteSpace(aggregation.Field))
        {
            throw BusinessException.Throw(400, $"聚合函数 {func} 需要指定 field");
        }

        var expr = func is "quantile" or "avg" or "sum"
            ? NumericExpression(aggregation.Field, domain, schema)
            : OrderableExpression(aggregation.Field, domain, schema);

        if (func == "quantile")
        {
            var q = aggregation.Q ?? 0.5;
            if (q is <= 0 or > 1)
            {
                throw BusinessException.Throw(400, "quantile 的 q 取值需满足 0 < q <= 1");
            }

            var qName = $"q{parameters.Count}";
            parameters.Add(new DuckDBParameter(qName, q));
            return $"quantile_cont({expr}, ${qName}) AS {alias}";
        }

        return $"{func.ToUpperInvariant()}({expr}) AS {alias}";
    }

    private static List<string> BuildFilters(List<QueryFilter> filters, DataDomain domain, Dictionary<string, FieldSpec> schema, List<DuckDBParameter> parameters)
    {
        var where = new List<string>();
        foreach (var filter in filters)
        {
            where.Add(BuildFilter(filter, domain, schema, parameters));
        }

        return where;
    }

    private static string BuildFilter(QueryFilter filter, DataDomain domain, Dictionary<string, FieldSpec> schema, List<DuckDBParameter> parameters)
    {
        var op = filter.Op?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!AllowedOps.Contains(op))
        {
            throw BusinessException.Throw(400, $"不支持的过滤操作符：{filter.Op}（白名单 eq/neq/in/prefix/match/gt/gte/lt/lte）");
        }

        var spec = ResolveField(filter.Field, domain, schema);
        var values = FilterValues(filter);

        if (op == OpIn)
        {
            if (values.Count == 0)
            {
                throw BusinessException.Throw(400, $"IN 过滤至少需要一个值：{filter.Field}");
            }

            var names = new List<string>();
            foreach (var raw in values)
            {
                var name = FilterParamName(parameters);
                parameters.Add(new DuckDBParameter(name, BindValue(raw, spec.Kind, filter.Field)));
                names.Add($"${name}");
            }

            return $"{spec.Expression} IN ({string.Join(", ", names)})";
        }

        if (values.Count != 1)
        {
            throw BusinessException.Throw(400, $"操作符 {op} 只接受单个值：{filter.Field}");
        }

        var paramName = FilterParamName(parameters);
        if (ComparisonOps.Contains(op))
        {
            // 比较：数值/时间字段直接比较；JSON 字段经 TRY_CAST 按数值比较（绑定值同为数值，非数值行不参与）；字符串无比较语义
            var expr = spec.Kind switch
            {
                FieldKind.Json => $"TRY_CAST({spec.Expression} AS DOUBLE)",
                FieldKind.String => throw BusinessException.Throw(400, $"字符串字段不支持比较操作符 {op}：{filter.Field}（可用 eq/neq/in/prefix/match）"),
                _ => spec.Expression,
            };
            object bound = BindComparable(values[0], spec.Kind, filter.Field);
            parameters.Add(new DuckDBParameter(paramName, bound));
            var sqlOp = op switch
            {
                OpGt => ">",
                OpGte => ">=",
                OpLt => "<",
                _ => "<=",
            };
            return $"{expr} {sqlOp} ${paramName}";
        }

        var boundValue = op == OpMatch
            ? $"%{EscapeLike(values[0])}%"
            : BindValue(values[0], spec.Kind, filter.Field);
        parameters.Add(new DuckDBParameter(paramName, boundValue));
        return op switch
        {
            OpEq => $"{spec.Expression} = ${paramName}",
            OpNeq => $"{spec.Expression} != ${paramName}",
            OpPrefix => $"starts_with({spec.Expression}, ${paramName})",
            OpMatch => $"{spec.Expression} LIKE ${paramName}",
            _ => throw BusinessException.Throw(400, $"不支持的过滤操作符：{filter.Op}"),
        };
    }

    /// <summary>
    /// 比较操作符绑定（数值/时间/长整型；雪花 id 以 long 绑定避免 double 精度丢失）.
    /// </summary>
    private static object BindComparable(string raw, FieldKind kind, string field)
    {
        return kind switch
        {
            FieldKind.Long => BindLong(raw, field),
            FieldKind.Time => BindTime(raw, field),
            FieldKind.Json => BindNumber(raw, field),
            _ => BindNumber(raw, field),
        };
    }

    private static long BindLong(string raw, string field)
    {
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw BusinessException.Throw(400, $"长整型字段过滤值必须是整数：{field} = {raw}");
        }

        return value;
    }

    private static string BuildRawOrderBy(List<QuerySort> sort, DataDomain domain, Dictionary<string, FieldSpec> schema)
    {
        if (sort.Count == 0)
        {
            return " ORDER BY ts DESC";
        }

        var parts = sort.Select(item =>
        {
            var spec = ResolveField(item.Field, domain, schema);
            return $"{spec.Expression} {(item.Desc ? "DESC" : "ASC")}";
        });
        return " ORDER BY " + string.Join(", ", parts);
    }

    private static string BuildAggregateOrderBy(List<QuerySort> sort, HashSet<string> aliases, bool hasBucket)
    {
        if (sort.Count == 0)
        {
            return hasBucket ? " ORDER BY \"bucket\"" : string.Empty;
        }

        var parts = sort.Select(item =>
        {
            var alias = QuoteAlias(item.Field, allowDot: true);
            if (!aliases.Contains(alias))
            {
                throw BusinessException.Throw(400, $"聚合模式排序仅允许输出别名：{item.Field}");
            }

            return $"{alias} {(item.Desc ? "DESC" : "ASC")}";
        });
        return " ORDER BY " + string.Join(", ", parts);
    }

    private static void ResolveLimit(int? limit, List<DuckDBParameter> parameters)
    {
        var value = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        if (parameters.All(p => p.ParameterName != "limit"))
        {
            parameters.Add(new DuckDBParameter("limit", value));
        }
    }

    private static string FilterParamName(List<DuckDBParameter> parameters)
    {
        // 前置 seed 固定为 start/end 两个参数，过滤参数从 p0 起算（过滤阶段先于聚合 q 参数执行）
        return $"p{parameters.Count - SeedParameterCount}";
    }

    private static string WhereClause(List<string> where)
    {
        return where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty;
    }

    private static string Union(ResolvedTables resolved, DataDomain domain)
    {
        return BuildBranchUnion(resolved, Projections[domain]);
    }

    /// <summary>
    /// ResolvedTables → 本地天表 + 冷层（Glob 兜底 / ColdFiles 逐分片列表）统一 UNION ALL 分支串（唯一冷层拼装实现）.
    /// 冷层路径来自台账/配置（非用户输入），单引号转义防破坏 SQL 结构；union_by_name 兼容历史归档对象缺列；
    /// ts 谓词保持 $start/$end 参数绑定（调用方负责绑定这两个参数）.
    /// </summary>
    /// <param name="resolved">表解析结果.</param>
    /// <param name="projection">SELECT 投影列清单.</param>
    /// <returns>UNION ALL 分支串（无数据源时为空串）.</returns>
    internal static string BuildBranchUnion(ResolvedTables resolved, string projection)
    {
        var branches = resolved.LocalTables.Select(table => $"SELECT {projection} FROM {table} WHERE ts >= $start AND ts < $end").ToList();
        foreach (var glob in resolved.ColdParquetGlobs)
        {
            var escaped = glob.Replace("'", "''");
            branches.Add($"SELECT {projection} FROM read_parquet('{escaped}', union_by_name=true) WHERE ts >= $start AND ts < $end");
        }

        if (resolved.ColdFiles.Count > 0)
        {
            // 台账驱动的逐分片精确路径：列表形式合入单分支（不产生逐分片 UNION 层级）；ColdFiles 非空时 Glob 兜底恒空（解析器互斥保证，防重复扫描）
            var files = string.Join(", ", resolved.ColdFiles.Select(file => $"'{file.Replace("'", "''")}'"));
            branches.Add($"SELECT {projection} FROM read_parquet([{files}], union_by_name=true) WHERE ts >= $start AND ts < $end");
        }

        return string.Join(" UNION ALL ", branches);
    }

    private static FieldSpec ResolveField(string field, DataDomain domain, Dictionary<string, FieldSpec> schema)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            throw BusinessException.Throw(400, "字段名不能为空");
        }

        if (schema.TryGetValue(field, out var spec))
        {
            return spec;
        }

        var dot = field.IndexOf('.');
        if (dot > 0)
        {
            var head = field[..dot];
            var path = field[(dot + 1)..];
            if (schema.TryGetValue(head, out var parent) && parent.Kind == FieldKind.Json)
            {
                if (!Regex.IsMatch(path, @"^[A-Za-z0-9_.\-]+$"))
                {
                    throw BusinessException.Throw(400, $"非法 JSON 路径：{field}");
                }

                return new FieldSpec($"json_extract_string({parent.Expression}, '$.\"{path}\"')", FieldKind.Json, IsJsonPath: true);
            }
        }

        throw BusinessException.Throw(400, $"未知字段：{field}（可用物理字段或 {JsonHeads(schema)} 前缀的 JSON 路径）");
    }

    private static string JsonHeads(Dictionary<string, FieldSpec> schema)
    {
        return string.Join("/", schema.Where(kv => kv.Value.Kind == FieldKind.Json).Select(kv => kv.Key + "."));
    }

    private static List<string> FilterValues(QueryFilter filter)
    {
        if (filter.Value is not JsonElement element)
        {
            throw BusinessException.Throw(400, $"过滤条件缺少 value：{filter.Field}");
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return [element.GetString() ?? string.Empty];
            case JsonValueKind.Number:
                return [element.GetRawText()];
            case JsonValueKind.Array:
                var values = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    {
                        values.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.GetRawText());
                    }
                    else
                    {
                        throw BusinessException.Throw(400, $"IN 过滤值仅支持字符串或数字：{filter.Field}");
                    }
                }

                return values;
            default:
                throw BusinessException.Throw(400, $"不支持的过滤值类型：{filter.Field}");
        }
    }

    private static object BindValue(string raw, FieldKind kind, string field)
    {
        return kind switch
        {
            FieldKind.Number => BindNumber(raw, field),
            FieldKind.Long => BindLong(raw, field),
            FieldKind.Time => BindTime(raw, field),
            _ => raw,
        };
    }

    private static double BindNumber(string raw, string field)
    {
        if (!double.TryParse(raw, CultureInfo.InvariantCulture, out var value))
        {
            throw BusinessException.Throw(400, $"数值字段过滤值必须是数字：{field} = {raw}");
        }

        return value;
    }

    private static DateTime BindTime(string raw, string field)
    {
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
        {
            throw BusinessException.Throw(400, $"时间字段过滤值必须是 ISO 8601：{field} = {raw}");
        }

        return ToDb(time);
    }

    private static string NumericExpression(string field, DataDomain domain, Dictionary<string, FieldSpec> schema)
    {
        var spec = ResolveField(field, domain, schema);
        return spec.Kind switch
        {
            FieldKind.Number => spec.Expression,
            FieldKind.Json => $"TRY_CAST({spec.Expression} AS DOUBLE)",
            FieldKind.Time => throw BusinessException.Throw(400, $"avg/sum/quantile 不支持时间字段：{field}"),
            _ => throw BusinessException.Throw(400, $"avg/sum/quantile 仅支持数值字段：{field}"),
        };
    }

    private static string OrderableExpression(string field, DataDomain domain, Dictionary<string, FieldSpec> schema)
    {
        var spec = ResolveField(field, domain, schema);
        return spec.Kind == FieldKind.Json ? $"TRY_CAST({spec.Expression} AS DOUBLE)" : spec.Expression;
    }

    private static string ParseStep(string step)
    {
        var match = Regex.Match(step?.Trim() ?? string.Empty, @"^(\d+)([smhd])$");
        if (!match.Success)
        {
            throw BusinessException.Throw(400, $"非法分桶步长：{step}（形如 30s/5m/1h/1d）");
        }

        var value = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (value is <= 0 or > 100_000)
        {
            throw BusinessException.Throw(400, $"分桶步长数值超出范围：{step}");
        }

        var name = match.Groups[2].Value switch
        {
            "s" => "second",
            "m" => "minute",
            "h" => "hour",
            _ => "day",
        };
        return $"INTERVAL '{value} {name}{(value > 1 ? "s" : string.Empty)}'";
    }

    private static string EscapeLike(string value)
    {
        return value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }

    private static string QuoteAlias(string alias, bool allowDot)
    {
        if (string.IsNullOrWhiteSpace(alias)
            || !Regex.IsMatch(alias, allowDot ? @"^[A-Za-z_][A-Za-z0-9_.]*$" : @"^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            throw BusinessException.Throw(400, $"非法输出列别名：{alias}");
        }

        return $"\"{alias.Replace("\"", "\"\"")}\"";
    }

    private static DateTime ToDb(DateTimeOffset time)
    {
        return time.ToUniversalTime().UtcDateTime;
    }
}
