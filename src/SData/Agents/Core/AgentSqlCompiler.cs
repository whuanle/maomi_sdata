using System.Globalization;
using System.Text.Json;
using SData.Infra.Exceptions;

namespace SData.Agents;

internal sealed record AgentSql(string Sql, Dictionary<string, object?> Parameters, List<string> Columns);

internal static class AgentSqlCompiler
{
    public static AgentSql Compile(AgentDataQuery query, bool aggregate)
    {
        var source = AgentDataCatalog.Source(query.Domain);
        var parameters = new Dictionary<string, object?> { ["cap"] = query.Limit + 1 };
        var where = Where(source, query, parameters);
        var select = new List<string>();
        var names = new List<string>();
        var groups = new List<string>();
        void Add(string expression, string name, bool group = false)
        {
            if (names.Contains(name, StringComparer.Ordinal)) throw BusinessException.Throw(400, "输出列名不能重复");
            var alias = "c" + names.Count.ToString(CultureInfo.InvariantCulture);
            select.Add(expression + " AS " + alias);
            names.Add(name);
            if (group) groups.Add(alias);
        }
        if (aggregate)
        {
            if (query.Fields.Count > 0) throw BusinessException.Throw(400, "聚合查询不接受明细投影字段");
            if (query.StepSeconds.HasValue)
            {
                parameters["step"] = query.StepSeconds.Value;
                Add("to_timestamp(floor(epoch(ts)/$step)*$step)", "bucket", true);
            }
            foreach (var name in query.GroupBy.Distinct(StringComparer.Ordinal))
                Add(AgentDataCatalog.Field(source, name).Name, name, true);
            var aggregations = query.Aggregations.Count == 0 ? [new AgentAggregation()] : query.Aggregations;
            foreach (var aggregation in aggregations)
            {
                if (string.IsNullOrWhiteSpace(aggregation.Alias) || !aggregation.Alias.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                    throw BusinessException.Throw(400, "统计别名只能包含英文字母、数字与下划线");
                var field = aggregation.Field == null ? null : AgentDataCatalog.Field(source, aggregation.Field);
                if (aggregation.Function != "count" && field == null) throw BusinessException.Throw(400, "统计函数缺少字段");
                if (aggregation.Function is "sum" or "avg" or "quantile" && field?.Numeric != true)
                    throw BusinessException.Throw(400, "统计函数只支持数值字段");
                var expression = aggregation.Function switch
                {
                    "count" => field == null ? "count(*)" : "count(" + field.Name + ")",
                    "distinct_count" => "count(DISTINCT " + field!.Name + ")",
                    "sum" => "sum(" + field!.Name + ")",
                    "avg" => "avg(" + field!.Name + ")",
                    "min" => "min(" + field!.Name + ")",
                    "max" => "max(" + field!.Name + ")",
                    "quantile" => Quantile(field!, aggregation.Quantile, parameters),
                    _ => throw BusinessException.Throw(400, "不支持的统计函数"),
                };
                Add(expression, aggregation.Alias);
            }
        }
        else
        {
            if (query.GroupBy.Count > 0 || query.Aggregations.Count > 0 || query.StepSeconds.HasValue)
                throw BusinessException.Throw(400, "明细查询不接受分组、聚合或时间桶，请调用 aggregate_data");
            var fields = query.Fields.Count == 0 ? source.Fields.Where(f => f.Readable).Select(f => f.Name) : query.Fields;
            foreach (var name in fields.Distinct(StringComparer.Ordinal))
            {
                var field = AgentDataCatalog.Field(source, name);
                Add(field.Type == "VARCHAR" ? "left(" + field.Name + ",2049)" : field.Name, name);
            }
        }
        var sorts = new List<string>();
        foreach (var sort in query.Sort)
        {
            var index = names.IndexOf(sort.Field);
            if (index < 0) throw BusinessException.Throw(400, "排序字段必须在输出列中");
            sorts.Add("c" + index.ToString(CultureInfo.InvariantCulture) + (sort.Descending ? " DESC" : " ASC") + " NULLS LAST");
        }
        if (sorts.Count == 0)
        {
            var index = names.IndexOf(aggregate ? (query.StepSeconds.HasValue ? "bucket" : names.Last()) : "ts");
            if (index >= 0) sorts.Add("c" + index.ToString(CultureInfo.InvariantCulture) + (query.StepSeconds.HasValue ? " ASC" : " DESC"));
        }
        var sql = "SELECT " + string.Join(",", select) + " FROM {{" + source.Domain + "}} WHERE " + where;
        if (groups.Count > 0) sql += " GROUP BY " + string.Join(",", groups);
        if (sorts.Count > 0) sql += " ORDER BY " + string.Join(",", sorts);
        sql += " LIMIT $cap";
        return new(sql, parameters, names);
    }

    public static string Where(AgentSource source, AgentDataQuery query, Dictionary<string, object?> parameters)
    {
        var parts = new List<string> { "ts >= $start AND ts < $end" };
        if (source.Domain is "sessions" or "rum_events")
        {
            if (string.IsNullOrWhiteSpace(query.Project)) throw BusinessException.Throw(400, "RUM 查询必须指定 project");
            parameters["project"] = query.Project;
            parts.Add("project_key = $project");
        }
        foreach (var filter in query.Filters)
        {
            var field = AgentDataCatalog.Field(source, filter.Field);
            if (filter.Value.ValueKind is JsonValueKind.Null)
            {
                parts.Add(filter.Op switch { "eq" => field.Name + " IS NULL", "neq" => field.Name + " IS NOT NULL", _ => throw BusinessException.Throw(400, "null 仅支持 eq/neq") });
                continue;
            }
            if (filter.Op == "in")
            {
                if (filter.Value.ValueKind != JsonValueKind.Array || filter.Value.GetArrayLength() is < 1 or > 20)
                    throw BusinessException.Throw(400, "in 必须包含1至20个值");
                var values = filter.Value.EnumerateArray().Select(value => Bind(parameters, Value(field, value))).ToList();
                parts.Add(field.Name + " IN (" + string.Join(",", values) + ")");
                continue;
            }
            var parameter = Bind(parameters, Value(field, filter.Value));
            parts.Add(filter.Op switch
            {
                "eq" => field.Name + " = " + parameter,
                "neq" => field.Name + " <> " + parameter,
                "gt" => field.Name + " > " + parameter,
                "gte" => field.Name + " >= " + parameter,
                "lt" => field.Name + " < " + parameter,
                "lte" => field.Name + " <= " + parameter,
                "prefix" when field.Type == "VARCHAR" => "starts_with(" + field.Name + "," + parameter + ")",
                "contains" when field.Type == "VARCHAR" => "contains(" + field.Name + "," + parameter + ")",
                _ => throw BusinessException.Throw(400, "不支持的过滤操作"),
            });
        }
        return string.Join(" AND ", parts);
    }

    private static string Quantile(AgentField field, double value, Dictionary<string, object?> parameters)
        => "quantile_cont(" + field.Name + "," + Bind(parameters, value) + ")";

    private static string Bind(Dictionary<string, object?> parameters, object? value)
    {
        var name = "p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
        parameters[name] = value;
        return "$" + name;
    }

    private static object Value(AgentField field, JsonElement value)
    {
        if (field.Type is "BIGINT" or "INTEGER" && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer)) return integer;
        if (field.Type == "DOUBLE" && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)) return number;
        if (field.Type == "TIMESTAMP" && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var time)) return time.UtcDateTime;
        if (field.Type == "VARCHAR" && value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 512) return value.GetString()!;
        throw BusinessException.Throw(400, "过滤值类型或长度与字段不匹配");
    }
}
