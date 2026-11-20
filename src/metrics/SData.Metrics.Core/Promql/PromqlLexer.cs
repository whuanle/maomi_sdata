using System.Text;

namespace SData.Metrics.Promql;

/// <summary>
/// PromQL 词法单元.
/// </summary>
public enum PromqlTokenKind
{
    /// <summary>标识符/关键字（metric 名、函数名、by/without/offset）.</summary>
    Ident,

    /// <summary>数字字面量.</summary>
    Number,

    /// <summary>带引号字符串.</summary>
    String,

    /// <summary>{</summary>
    LBrace,

    /// <summary>}</summary>
    RBrace,

    /// <summary>(</summary>
    LParen,

    /// <summary>)</summary>
    RParen,

    /// <summary>[</summary>
    LBracket,

    /// <summary>]</summary>
    RBracket,

    /// <summary>,</summary>
    Comma,

    /// <summary>=</summary>
    Eq,

    /// <summary>!=</summary>
    Neq,

    /// <summary>=~</summary>
    RegexMatch,

    /// <summary>!~</summary>
    RegexNotMatch,

    /// <summary>输入结束.</summary>
    Eof,
}

/// <summary>
/// PromQL 词素.
/// </summary>
public readonly record struct PromqlToken(PromqlTokenKind Kind, string Text, int Position);

/// <summary>
/// PromQL 子集词法分析（标识符/数字/字符串/符号；非法字符中文报错）.
/// </summary>
public static class PromqlLexer
{
    /// <summary>
    /// 分词.
    /// </summary>
    /// <param name="input">表达式.</param>
    /// <returns>token 序列（末尾含 Eof）.</returns>
    public static List<PromqlToken> Tokenize(string input)
    {
        var tokens = new List<PromqlToken>();
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // 标识符：PromQL metric 名与函数名（含 :）
            if (char.IsLetter(c) || c == '_' || c == ':')
            {
                var start = i;
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_' || input[i] == ':'))
                {
                    i++;
                }

                tokens.Add(new PromqlToken(PromqlTokenKind.Ident, input[start..i], start));
                continue;
            }

            // 数字（含科学计数法与 .5 形态）
            if (char.IsDigit(c) || (c == '.' && i + 1 < input.Length && char.IsDigit(input[i + 1])))
            {
                var start = i;
                while (i < input.Length && (char.IsDigit(input[i]) || input[i] == '.'))
                {
                    i++;
                }

                if (i < input.Length && (input[i] is 'e' or 'E'))
                {
                    var mark = i;
                    i++;
                    if (i < input.Length && input[i] is '+' or '-')
                    {
                        i++;
                    }

                    if (i >= input.Length || !char.IsDigit(input[i]))
                    {
                        i = mark;
                    }
                    else
                    {
                        while (i < input.Length && char.IsDigit(input[i]))
                        {
                            i++;
                        }
                    }
                }

                tokens.Add(new PromqlToken(PromqlTokenKind.Number, input[start..i], start));
                continue;
            }

            // 字符串（'..' 或 ".."，支持 \" \\ 转义）
            if (c is '"' or '\'')
            {
                var start = i;
                var quote = c;
                i++;
                var sb = new StringBuilder();
                while (i < input.Length && input[i] != quote)
                {
                    if (input[i] == '\\' && i + 1 < input.Length)
                    {
                        sb.Append(input[i + 1]);
                        i += 2;
                        continue;
                    }

                    sb.Append(input[i]);
                    i++;
                }

                if (i >= input.Length)
                {
                    throw new PromqlParseException(start, "字符串未闭合");
                }

                i++;
                tokens.Add(new PromqlToken(PromqlTokenKind.String, sb.ToString(), start));
                continue;
            }

            switch (c)
            {
                case '{':
                    tokens.Add(new PromqlToken(PromqlTokenKind.LBrace, "{", i));
                    i++;
                    break;
                case '}':
                    tokens.Add(new PromqlToken(PromqlTokenKind.RBrace, "}", i));
                    i++;
                    break;
                case '(':
                    tokens.Add(new PromqlToken(PromqlTokenKind.LParen, "(", i));
                    i++;
                    break;
                case ')':
                    tokens.Add(new PromqlToken(PromqlTokenKind.RParen, ")", i));
                    i++;
                    break;
                case '[':
                    tokens.Add(new PromqlToken(PromqlTokenKind.LBracket, "[", i));
                    i++;
                    break;
                case ']':
                    tokens.Add(new PromqlToken(PromqlTokenKind.RBracket, "]", i));
                    i++;
                    break;
                case ',':
                    tokens.Add(new PromqlToken(PromqlTokenKind.Comma, ",", i));
                    i++;
                    break;
                case '=' when i + 1 < input.Length && input[i + 1] == '~':
                    tokens.Add(new PromqlToken(PromqlTokenKind.RegexMatch, "=~", i));
                    i += 2;
                    break;
                case '!' when i + 1 < input.Length && input[i + 1] == '~':
                    tokens.Add(new PromqlToken(PromqlTokenKind.RegexNotMatch, "!~", i));
                    i += 2;
                    break;
                case '!' when i + 1 < input.Length && input[i + 1] == '=':
                    tokens.Add(new PromqlToken(PromqlTokenKind.Neq, "!=", i));
                    i += 2;
                    break;
                case '=':
                    tokens.Add(new PromqlToken(PromqlTokenKind.Eq, "=", i));
                    i++;
                    break;
                default:
                    throw new PromqlParseException(i, $"非法字符 \"{c}\"");
            }
        }

        tokens.Add(new PromqlToken(PromqlTokenKind.Eof, string.Empty, input.Length));
        return tokens;
    }
}
