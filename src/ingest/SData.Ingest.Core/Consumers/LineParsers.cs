using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SData.Infra.Configuration;

namespace SData.Ingest.Consumers;

/// <summary>
/// 单行解析器委托：行文本 → 字段字典（regex 命名捕获组 / csv 列索引 / logfmt 键 / syslog 结构化字段）.
/// </summary>
/// <param name="line">行文本.</param>
/// <param name="parser">解析规则参数.</param>
/// <param name="fields">解析出的字段字典.</param>
/// <param name="error">失败原因（人类可读中文；成功为 null）.</param>
/// <returns>是否解析成功.</returns>
public delegate bool TryParseLine(string line, MqParserOptions? parser, out Dictionary<string, string> fields, out string? error);

/// <summary>
/// regex 行解析器：命名捕获组 → 字段字典（未命名组忽略）；正则按 Pattern 缓存编译，匹配超时按失败处理.
/// </summary>
public static class RegexLineParser
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 解析单行.
    /// </summary>
    /// <param name="line">行文本.</param>
    /// <param name="parser">解析规则参数（Pattern 必填）.</param>
    /// <param name="fields">字段字典.</param>
    /// <param name="error">失败原因.</param>
    /// <returns>是否成功.</returns>
    public static bool TryParse(string line, MqParserOptions? parser, out Dictionary<string, string> fields, out string? error)
    {
        fields = [];
        error = null;
        var pattern = parser?.Pattern;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "regex 解析规则未配置正则表达式";
            return false;
        }

        Regex regex;
        try
        {
            regex = Cache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)));
        }
        catch (ArgumentException)
        {
            error = "正则表达式无法编译";
            return false;
        }

        Match match;
        try
        {
            match = regex.Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            error = "正则匹配超时（回溯过长）";
            return false;
        }

        if (!match.Success)
        {
            error = "正则未匹配该行";
            return false;
        }

        foreach (var name in regex.GetGroupNames())
        {
            if (int.TryParse(name, out _))
            {
                continue;
            }

            var group = match.Groups[name];
            if (group.Success)
            {
                fields[name] = group.Value;
            }
        }

        return true;
    }
}

/// <summary>
/// csv 行解析器：按分隔符分列，字段键为列索引字符串（"0"/"1"/…），映射配置的 TimestampField 等填列索引.
/// </summary>
public static class CsvLineParser
{
    /// <summary>
    /// 解析单行.
    /// </summary>
    /// <param name="line">行文本.</param>
    /// <param name="parser">解析规则参数（Delimiter，缺省逗号）.</param>
    /// <param name="fields">字段字典（键 = 列索引）.</param>
    /// <param name="error">失败原因.</param>
    /// <returns>是否成功.</returns>
    public static bool TryParse(string line, MqParserOptions? parser, out Dictionary<string, string> fields, out string? error)
    {
        fields = [];
        error = null;
        var delimiter = string.IsNullOrEmpty(parser?.Delimiter) ? ',' : parser!.Delimiter[0];
        var columns = line.Split(delimiter);
        for (var index = 0; index < columns.Length; index++)
        {
            fields[index.ToString(CultureInfo.InvariantCulture)] = columns[index];
        }

        return columns.Length > 1 || columns[0].Length > 0;
    }
}

/// <summary>
/// logfmt 行解析器：空格分隔的 key=value 片段（引号值支持含空格），无 '=' 的片段忽略.
/// </summary>
public static class LogfmtLineParser
{
    /// <summary>
    /// 解析单行.
    /// </summary>
    /// <param name="line">行文本.</param>
    /// <param name="parser">解析规则参数（未使用）.</param>
    /// <param name="fields">字段字典.</param>
    /// <param name="error">失败原因.</param>
    /// <returns>是否成功.</returns>
    public static bool TryParse(string line, MqParserOptions? parser, out Dictionary<string, string> fields, out string? error)
    {
        fields = [];
        error = null;
        foreach (var token in SplitTokens(line))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var value = token[(eq + 1)..];
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            fields[token[..eq]] = value;
        }

        if (fields.Count == 0)
        {
            error = "logfmt 未解析出 key=value 片段";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 按空白拆分 token（引号内的空白不拆分）.
    /// </summary>
    private static List<string> SplitTokens(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var character in line)
        {
            if (quote != null)
            {
                current.Append(character);
                if (character == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                current.Append(character);
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}

/// <summary>
/// syslog 行解析器：RFC5424（&lt;PRI&gt;VER TIMESTAMP HOST APP PROCID MSGID SD MSG）与 RFC3164（&lt;PRI&gt;Mmm dd hh:mm:ss HOST TAG[PID]: MSG）自动识别.
/// 统一产出字段：pri（原始 PRI）、severity（PRI%8，0-7）、timestamp、host、tag（5424 取 app）、pid（5424 取 procid）、msg、sd.
/// </summary>
public static class SyslogLineParser
{
    /// <summary>RFC5424 匹配（SD 部分为 - 或若干 [...] 段）.</summary>
    private static readonly Regex Rfc5424 = new(
        "^<(\\d{1,3})>\\d{1,2}\\s+(\\S+)\\s+(\\S+)\\s+(\\S+)\\s+(\\S+)\\s+(\\S+)\\s+(-|(?:\\[.*?\\])+)(?:\\s(.*))?$",
        RegexOptions.Compiled | RegexOptions.Singleline,
        TimeSpan.FromMilliseconds(200));

    /// <summary>RFC3164 匹配（tag 可选 [pid]，冒号可选）.</summary>
    private static readonly Regex Rfc3164 = new(
        "^<(\\d{1,3})>(\\w{3}\\s+\\d{1,2}\\s+\\d{2}:\\d{2}:\\d{2})\\s+(\\S+)\\s+([^:\\s\\[]+)(?:\\[(\\d+)\\])?:?\\s*(.*)$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// 解析单行.
    /// </summary>
    /// <param name="line">行文本.</param>
    /// <param name="parser">解析规则参数（未使用；默认映射由 EffectiveParser 应用）.</param>
    /// <param name="fields">字段字典.</param>
    /// <param name="error">失败原因.</param>
    /// <returns>是否成功.</returns>
    public static bool TryParse(string line, MqParserOptions? parser, out Dictionary<string, string> fields, out string? error)
    {
        fields = [];
        error = null;
        var match = Rfc5424.Match(line);
        if (match.Success)
        {
            AddCommon(fields, match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
            fields["tag"] = match.Groups[4].Value;
            fields["pid"] = match.Groups[5].Value;
            if (match.Groups[7].Success && match.Groups[7].Value != "-")
            {
                fields["sd"] = match.Groups[7].Value;
            }

            fields["msg"] = match.Groups[8].Success ? match.Groups[8].Value : string.Empty;
            return true;
        }

        match = Rfc3164.Match(line);
        if (match.Success)
        {
            AddCommon(fields, match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
            fields["tag"] = match.Groups[4].Value;
            if (match.Groups[5].Success)
            {
                fields["pid"] = match.Groups[5].Value;
            }

            fields["msg"] = match.Groups[6].Value;
            return true;
        }

        error = "既不符合 RFC5424 也不符合 RFC3164";
        return false;
    }

    /// <summary>
    /// syslog 的生效映射参数：未配置字段时应用惯例默认（tag→service、severity→级别、timestamp→时间、msg→正文）.
    /// </summary>
    /// <param name="parser">用户配置的解析参数（可空）.</param>
    /// <returns>补齐默认映射的副本（不修改原配置对象）.</returns>
    public static MqParserOptions EffectiveParser(MqParserOptions? parser)
    {
        var source = parser ?? new MqParserOptions();
        return new MqParserOptions
        {
            TimestampField = IfEmpty(source.TimestampField, "timestamp"),
            TimestampFormat = source.TimestampFormat,
            SeverityField = IfEmpty(source.SeverityField, "severity"),
            SeverityMap = source.SeverityMap,
            ServiceField = IfEmpty(source.ServiceField, "tag"),
            BodyField = IfEmpty(source.BodyField, "msg"),
            TraceIdField = source.TraceIdField,
            SpanIdField = source.SpanIdField,
            Pattern = source.Pattern,
            Delimiter = source.Delimiter,
            KeepUnmapped = source.KeepUnmapped,
        };
    }

    private static void AddCommon(Dictionary<string, string> fields, string pri, string timestamp, string host)
    {
        fields["pri"] = pri;
        fields["severity"] = (int.Parse(pri, CultureInfo.InvariantCulture) % 8).ToString(CultureInfo.InvariantCulture);
        fields["timestamp"] = timestamp;
        fields["host"] = host;
    }

    private static string IfEmpty(string value, string fallback)
    {
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
}
