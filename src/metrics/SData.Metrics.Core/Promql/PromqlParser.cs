namespace SData.Metrics.Promql;

/// <summary>
/// PromQL 子集解析器（递归下降；语法范围 = T710 任务卡子集）.
/// </summary>
public sealed class PromqlParser
{
    private static readonly string[] AggregateOps = { "sum", "avg", "min", "max", "count", "topk", "bottomk" };
    private static readonly string[] Functions = { "rate" };

    private readonly List<PromqlToken> _tokens;
    private int _pos;

    /// <summary>
    /// Initializes a new instance of the <see cref="PromqlParser"/> class.
    /// </summary>
    /// <param name="input">表达式文本.</param>
    public PromqlParser(string input)
    {
        _tokens = PromqlLexer.Tokenize(input);
    }

    /// <summary>
    /// 解析表达式；非法输入抛 <see cref="PromqlParseException"/>（中文消息）.
    /// </summary>
    /// <param name="input">表达式文本.</param>
    /// <returns>AST 根节点.</returns>
    public static PromqlNode Parse(string input)
    {
        var parser = new PromqlParser(input);
        return parser.ParseExpr();
    }

    private PromqlNode ParseExpr()
    {
        if (_tokens[_pos].Kind == PromqlTokenKind.Eof)
        {
            throw new PromqlParseException(_tokens[_pos].Position, "表达式为空");
        }

        var token = _tokens[_pos];
        if (token.Kind == PromqlTokenKind.Ident)
        {
            var word = token.Text;
            if (AggregateOps.Contains(word))
            {
                return ParseAggregate();
            }

            if (Functions.Contains(word) && _tokens[_pos + 1].Kind == PromqlTokenKind.LParen)
            {
                return ParseCall();
            }

            return ParseSelector();
        }

        if (token.Kind == PromqlTokenKind.Number)
        {
            _pos++;
            return new PromqlNumber(double.Parse(token.Text, System.Globalization.CultureInfo.InvariantCulture)) { Position = token.Position };
        }

        if (token.Kind == PromqlTokenKind.LParen)
        {
            _pos++;
            var inner = ParseExpr();
            Expect(PromqlTokenKind.RParen, "缺少右括号");
            return inner;
        }

        throw new PromqlParseException(token.Position, $"意外的符号 \"{token.Text}\"");
    }

    private PromqlNode ParseAggregate()
    {
        var opToken = _tokens[_pos];
        _pos++;
        IReadOnlyList<string>? grouping = null;
        var without = false;

        // by/without 修饰符（位于参数括号前）
        if (IsKeyword("by") || IsKeyword("without"))
        {
            without = _tokens[_pos].Text == "without";
            _pos++;
            Expect(PromqlTokenKind.LParen, "by/without 后缺少标签列表括号");
            grouping = ParseLabelList();
            Expect(PromqlTokenKind.RParen, "标签列表缺少右括号");
        }

        Expect(PromqlTokenKind.LParen, "聚合缺少参数括号");
        int? param = null;
        if (_tokens[_pos].Kind == PromqlTokenKind.Number)
        {
            var numberToken = _tokens[_pos];
            param = (int)double.Parse(numberToken.Text, System.Globalization.CultureInfo.InvariantCulture);
            _pos++;
            Expect(PromqlTokenKind.Comma, "topk/bottomk 参数后缺少逗号");
        }

        var argument = ParseExpr();
        Expect(PromqlTokenKind.RParen, "聚合缺少右括号");

        if ((opToken.Text == "topk" || opToken.Text == "bottomk") && param == null)
        {
            throw new PromqlParseException(opToken.Position, "topk/bottomk 需要数量参数（如 topk(3, ...)）");
        }

        return new PromqlAggregate(opToken.Text, argument, grouping, without, param) { Position = opToken.Position };
    }

    private PromqlNode ParseCall()
    {
        var fnToken = _tokens[_pos];
        _pos++;
        Expect(PromqlTokenKind.LParen, "函数缺少参数括号");
        var argument = ParseExpr();
        Expect(PromqlTokenKind.RParen, "函数缺少右括号");
        return new PromqlCall(fnToken.Text, argument) { Position = fnToken.Position };
    }

    private PromqlNode ParseSelector()
    {
        var metricToken = Expect(PromqlTokenKind.Ident, "缺少指标名");
        IReadOnlyList<PromqlLabelMatcher>? matchers = null;
        TimeSpan? range = null;
        TimeSpan? offset = null;

        if (_tokens[_pos].Kind == PromqlTokenKind.LBrace)
        {
            _pos++;
            matchers = ParseMatchers();
            Expect(PromqlTokenKind.RBrace, "标签匹配缺少右花括号");
        }

        if (_tokens[_pos].Kind == PromqlTokenKind.LBracket)
        {
            _pos++;
            range = ParseDuration();
            Expect(PromqlTokenKind.RBracket, "范围向量缺少右方括号");
        }

        if (IsKeyword("offset"))
        {
            _pos++;
            offset = ParseDuration();
        }

        return new PromqlVectorSelector(metricToken.Text, matchers ?? [], range, offset) { Position = metricToken.Position };
    }

    private IReadOnlyList<PromqlLabelMatcher> ParseMatchers()
    {
        var matchers = new List<PromqlLabelMatcher>();
        while (_tokens[_pos].Kind != PromqlTokenKind.RBrace)
        {
            var labelToken = Expect(PromqlTokenKind.Ident, "缺少标签名");
            var opToken = _tokens[_pos];
            var op = opToken.Kind switch
            {
                PromqlTokenKind.Eq => "=",
                PromqlTokenKind.Neq => "!=",
                PromqlTokenKind.RegexMatch => "=~",
                PromqlTokenKind.RegexNotMatch => "!~",
                _ => throw new PromqlParseException(opToken.Position, $"标签后缺少匹配操作符（= != =~ !~），收到 \"{opToken.Text}\""),
            };
            _pos++;
            var valueToken = Expect(PromqlTokenKind.String, "标签值必须是带引号字符串");
            matchers.Add(new PromqlLabelMatcher(labelToken.Text, op, valueToken.Text));
            if (_tokens[_pos].Kind == PromqlTokenKind.Comma)
            {
                _pos++;
                continue;
            }

            break;
        }

        return matchers;
    }

    private IReadOnlyList<string> ParseLabelList()
    {
        var labels = new List<string>();
        while (_tokens[_pos].Kind == PromqlTokenKind.Ident)
        {
            labels.Add(_tokens[_pos].Text);
            _pos++;
            if (_tokens[_pos].Kind == PromqlTokenKind.Comma)
            {
                _pos++;
                continue;
            }

            break;
        }

        return labels;
    }

    private TimeSpan ParseDuration()
    {
        var token = _tokens[_pos];
        if (token.Kind != PromqlTokenKind.Number)
        {
            throw new PromqlParseException(token.Position, $"时长必须是数字+单位（如 5m），收到 \"{token.Text}\"");
        }

        // 数字与单位粘写（5m）→ 词法上 "5m" 是 Ident（5 开头含字母）？不：词法把数字与字母分开，5m → Number("5") + Ident("m")
        var numberText = token.Text;
        _pos++;
        var unitToken = _tokens[_pos];
        var unit = unitToken.Kind == PromqlTokenKind.Ident && unitToken.Text.Length <= 2
            ? unitToken.Text
            : throw new PromqlParseException(unitToken.Position, $"缺少时长单位（ms/s/m/h/d/w/y），收到 \"{unitToken.Text}\"");
        _pos++;
        var value = double.Parse(numberText, System.Globalization.CultureInfo.InvariantCulture);
        return unit switch
        {
            "ms" => TimeSpan.FromMilliseconds(value),
            "s" => TimeSpan.FromSeconds(value),
            "m" => TimeSpan.FromMinutes(value),
            "h" => TimeSpan.FromHours(value),
            "d" => TimeSpan.FromDays(value),
            "w" => TimeSpan.FromDays(value * 7),
            "y" => TimeSpan.FromDays(value * 365),
            _ => throw new PromqlParseException(unitToken.Position, $"未知时长单位 \"{unit}\""),
        };
    }

    private bool IsKeyword(string keyword)
    {
        return _tokens[_pos].Kind == PromqlTokenKind.Ident && _tokens[_pos].Text == keyword;
    }

    private PromqlToken Expect(PromqlTokenKind kind, string error)
    {
        var token = _tokens[_pos];
        if (token.Kind != kind)
        {
            throw new PromqlParseException(token.Position, $"{error}，收到 \"{(token.Kind == PromqlTokenKind.Eof ? "<结束>" : token.Text)}\"");
        }

        _pos++;
        return token;
    }
}
