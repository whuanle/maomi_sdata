using System.Text;
using SData.Infra.Exceptions;
using SData.Store.QueryDsl;

namespace SData.Store.Core;

/// <summary>
/// 搜索 SQL 表占位符展开器.
/// 用户 SQL 中的 <c>{{logs}}</c>/<c>{{traces}}</c>/<c>{{metrics}}</c>/<c>{{iot}}</c> 占位符在字符串字面量之外
/// 展开为时间区间内现存按天表的 UNION ALL BY NAME 子查询（表名来自 TimeTables 前缀 + 内部日期，非用户输入）；
/// 同时收集字符串字面量之外引用的全部 $name 参数名（$start/$end 由调用方以时间范围绑定，其余由调用方以参数字典绑定）.
/// </summary>
public static class SearchSqlExpander
{
    /// <summary>表占位符名 → 按天表前缀（内部常量，非用户输入）.</summary>
    private static readonly Dictionary<string, string> Tokens = new(StringComparer.Ordinal)
    {
        ["logs"] = TimeTables.LogsPrefix,
        ["traces"] = TimeTables.TracesPrefix,
        ["metrics"] = TimeTables.MetricsPrefix,
        ["iot"] = TimeTables.IotPrefix,
        ["sessions"] = TimeTables.RumSessionPrefix,
        ["rum_events"] = TimeTables.RumEventPrefix,
    };

    /// <summary>
    /// 展开结果.
    /// </summary>
    /// <param name="Sql">展开后的 SQL.</param>
    /// <param name="UsesStart">字符串字面量之外是否引用了 $start.</param>
    /// <param name="UsesEnd">字符串字面量之外是否引用了 $end.</param>
    /// <param name="ReferencedParams">字符串字面量之外引用的全部参数名（不含 $ 前缀，按首次出现顺序去重，含 start/end）.</param>
    public sealed record ExpansionResult(string Sql, bool UsesStart, bool UsesEnd, IReadOnlyList<string> ReferencedParams);

    /// <summary>
    /// 展开表占位符并收集参数引用（统一查询管线：表来源 = TableResolver 解析结果，本地天表 + 冷层分片一并展开）.
    /// </summary>
    /// <param name="sql">用户 SQL.</param>
    /// <param name="resolveTables">表前缀 → 解析结果（本地天表 + 冷层 Glob/ColdFiles）；null 或全空视为区间无数据源.</param>
    /// <returns>展开结果；任一被引用的占位符无数据源时返回 null（调用方短路空结果）.</returns>
    public static ExpansionResult? Expand(string sql, Func<string, ResolvedTables?> resolveTables)
    {
        var builder = new StringBuilder(sql.Length);
        var usesStart = false;
        var usesEnd = false;
        var referenced = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\'')
            {
                // 字符串字面量原样拷贝（含 '' 转义），内部不做任何替换
                builder.Append(c);
                i++;
                while (i < sql.Length)
                {
                    builder.Append(sql[i]);
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            builder.Append('\'');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (c == '{' && i + 1 < sql.Length && sql[i + 1] == '{')
            {
                var close = sql.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    throw BusinessException.Throw(400, "占位符缺少闭合 }}：仅支持 {{logs}}/{{traces}}/{{metrics}}/{{iot}}/{{sessions}}/{{rum_events}}");
                }

                var name = sql[(i + 2)..close].Trim();
                if (!Tokens.TryGetValue(name, out var prefix))
                {
                    throw BusinessException.Throw(400, $"未知的数据表占位符：{{{{{name}}}}}（仅支持 {{logs}}/{{traces}}/{{metrics}}/{{iot}}/{{sessions}}/{{rum_events}}）");
                }

                var tables = resolveTables(prefix);
                if (tables == null || (tables.LocalTables.Count == 0 && tables.ColdParquetGlobs.Count == 0 && tables.ColdFiles.Count == 0))
                {
                    return null;
                }

                // 本地天表 + 冷层（Glob 兜底 / ColdFiles 逐分片列表）同一 UNION；
                // UNION ALL BY NAME 按列名合并（SELECT * 位置合并会在列序不同的天表间错位：
                // 旧版迁移表 id/source 追加在末尾，新表 id 在首列，BIGINT 对 TIMESTAMP 直接抛错）
                var parts = tables.LocalTables.Select(table => $"SELECT * FROM {table}").ToList();
                foreach (var glob in tables.ColdParquetGlobs)
                {
                    parts.Add($"SELECT * FROM read_parquet('{glob.Replace("'", "''")}', union_by_name=true)");
                }

                if (tables.ColdFiles.Count > 0)
                {
                    var files = string.Join(", ", tables.ColdFiles.Select(file => $"'{file.Replace("'", "''")}'"));
                    parts.Add($"SELECT * FROM read_parquet([{files}], union_by_name=true)");
                }

                builder.Append('(');
                builder.Append(string.Join(" UNION ALL BY NAME ", parts));
                builder.Append(')');
                i = close + 2;
                continue;
            }

            if (c == '$' && i + 1 < sql.Length && (char.IsAsciiLetter(sql[i + 1]) || sql[i + 1] == '_'))
            {
                var j = i + 1;
                while (j < sql.Length && (char.IsAsciiLetterOrDigit(sql[j]) || sql[j] == '_'))
                {
                    j++;
                }

                var name = sql[i..j];
                if (name == "$start")
                {
                    usesStart = true;
                }
                else if (name == "$end")
                {
                    usesEnd = true;
                }

                if (seen.Add(name))
                {
                    referenced.Add(name[1..]);
                }

                builder.Append(name);
                i = j;
                continue;
            }

            builder.Append(c);
            i++;
        }

        return new ExpansionResult(builder.ToString(), usesStart, usesEnd, referenced);
    }
}
