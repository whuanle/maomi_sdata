using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SData.Infra.Exceptions;

namespace SData.Store.Models;

/// <summary>告警 WHERE 子集：只允许单行纯函数、物理字段和绑定值，不接受数据源或子查询.</summary>
public static class AlertConditionCompiler
{
    private static readonly Regex Token = new(@"\G\s*(?:(?<str>'(?:[^']|'')*')|(?<param>\$[a-zA-Z_][a-zA-Z0-9_]*)|(?<num>\d+(?:\.\d+)?)|(?<id>[a-zA-Z_][a-zA-Z0-9_]*)|(?<op><=|>=|<>|!=|[=<>(),+\-*/]))", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "json_extract_string", "json_valid", "starts_with", "contains", "lower", "upper", "coalesce", "nullif", "try_cast", "cast",
    };
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "is", "null", "in", "like", "true", "false", "case", "when", "then", "else", "end", "as", "double", "bigint", "varchar",
    };

    public static (string Sql, Dictionary<string, object?> Parameters) Compile(string domain, string condition, IReadOnlyDictionary<string, object?> parameters)
    {
        var fields = domain switch
        {
            "logs" => "ts service severity body trace_id span_id source attributes resource",
            "traces" => "ts service name duration_us kind status trace_id span_id parent_span_id attributes resource events",
            _ => throw BusinessException.Throw(400, "告警仅支持日志和链路"),
        };
        if (condition == null || condition.Length > 8192 || parameters == null || parameters.Count > 128)
        {
            throw BusinessException.Throw(400, "告警条件过长或参数过多");
        }

        var allowed = fields.Split(' ').ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bound = new Dictionary<string, object?>();
        var sql = new StringBuilder();
        var offset = 0;
        var depth = 0;
        var tokens = 0;
        while (offset < condition.Length && !string.IsNullOrWhiteSpace(condition[offset..]))
        {
            var match = Token.Match(condition, offset);
            if (!match.Success || ++tokens > 512)
            {
                throw BusinessException.Throw(400, "告警条件包含不支持的语法（不支持子查询、表引用或注释）");
            }

            offset = match.Index + match.Length;
            var value = match.Value.Trim();
            if (match.Groups["str"].Success || match.Groups["num"].Success)
            {
                var key = "alert_literal_" + tokens.ToString(CultureInfo.InvariantCulture);
                bound[key] = match.Groups["str"].Success ? value[1..^1].Replace("''", "'", StringComparison.Ordinal) : decimal.Parse(value, CultureInfo.InvariantCulture);
                value = "$" + key;
            }
            else if (match.Groups["param"].Success)
            {
                var key = value[1..];
                if (key is "start" or "end" or "alert_cap" || key.StartsWith("alert_literal_", StringComparison.Ordinal) || !parameters.TryGetValue(key, out var parameter))
                {
                    throw BusinessException.Throw(400, "告警条件缺少参数或使用了保留参数名");
                }

                if (parameter is JsonElement json)
                {
                    parameter = json.ValueKind switch
                    {
                        JsonValueKind.String => json.GetString(),
                        JsonValueKind.Number => json.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => throw BusinessException.Throw(400, "告警参数必须为简单值"),
                    };
                }

                if (parameter is string str && str.Length > 2048 || parameter != null && parameter is not (string or bool or long or int or double or decimal))
                {
                    throw BusinessException.Throw(400, "告警参数类型或长度不支持");
                }

                bound[key] = parameter;
            }
            else if (match.Groups["id"].Success)
            {
                var next = condition[offset..].TrimStart();
                var isFunction = next.StartsWith('(') && !Keywords.Contains(value);
                if (isFunction ? !Functions.Contains(value) : !allowed.Contains(value) && !Keywords.Contains(value))
                {
                    throw BusinessException.Throw(400, "告警条件包含不支持的字段或函数");
                }
            }

            if (value == "(" && ++depth > 16 || value == ")" && --depth < 0)
            {
                throw BusinessException.Throw(400, "告警条件括号层级不合法");
            }

            sql.Append(value).Append(' ');
        }

        if (depth != 0)
        {
            throw BusinessException.Throw(400, "告警条件括号不匹配");
        }

        return (tokens == 0 ? "TRUE" : sql.ToString().Trim(), bound);
    }
}
