using System.Globalization;
using System.Text.RegularExpressions;

namespace SData.Dashboard;

/// <summary>自定义面板查询图；仅保存数据计算，不包含可执行前端代码.</summary>
public class CustomPanelDefinition
{
    public int Version { get; set; } = 1;
    public List<CustomPanelNode> Nodes { get; set; } = new();
    public List<string> Outputs { get; set; } = new();

    public static string? Validate(CustomPanelDefinition? definition, string? panelType = null)
    {
        if (definition == null || definition.Version != 1) return "不支持的自定义面板版本";
        if (definition.Nodes == null || definition.Outputs == null || definition.Nodes.Count is < 1 or > 20
            || definition.Nodes.Any(x => x == null)
            || definition.Nodes.Count(x => x.Kind == "sql") is < 1 or > 12
            || definition.Nodes.Count(x => x.Kind == "expression") > 8) return "自定义面板最多 12 条 SQL、8 个表达式";
        var refs = new HashSet<string>(StringComparer.Ordinal);
        var shapes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in definition.Nodes)
        {
            if (!Regex.IsMatch(node.Ref ?? "", "^[A-Z]{1,3}$") || !refs.Add(node.Ref!)) return "节点引用必须是唯一的大写字母";
            if (node.Alias?.Length > 128 || !CustomNodeEditorValidator.IsValid(node.Editor, node.Kind)) return "节点编辑定义版本、类型或大小无效";
            if (node.Kind == "sql")
            {
                if (string.IsNullOrWhiteSpace(node.Sql) || node.Sql.Length > 100_000 || node.Parameters == null || node.Parameters.Count > 64) return "SQL 或参数配置无效";
                if (node.Parameters.Any(x => !Regex.IsMatch(x.Key, "^[a-z][a-z0-9_]*$") || !ValidParameter(x.Value))) return "SQL 参数名或参数值无效";
                if (node.Shape is not ("table" or "scalar" or "series" or "category")) return "未知查询结果类型";
                if (node.GroupFields == null || node.GroupFields.Count > 20 || node.GroupFields.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256) || node.GroupFields.Distinct().Count() != node.GroupFields.Count) return "分组字段无效";
                if (node.Shape != "table" && string.IsNullOrWhiteSpace(node.ValueField)) return "请选择数值字段";
                if (node.Shape == "series" && string.IsNullOrWhiteSpace(node.TimeField)) return "请选择时间字段";
                shapes[node.Ref] = node.Shape;
            }
            else if (node.Kind == "expression")
            {
                if (node.Reduce is not ("" or "sum" or "avg" or "min" or "max" or "last" or "count")) return "未知归约方式";
                try
                {
                    var expression = PanelExpression.Parse(node.Expression, refs.Where(x => x != node.Ref));
                    var inputs = expression.References.Select(x => shapes[x]).Distinct().ToList();
                    if (inputs.Contains("table")) return "表格输入需先映射为标量或序列";
                    var vectors = inputs.Where(x => x != "scalar").ToList();
                    if (vectors.Count > 1) return "表达式输入类型不兼容";
                    shapes[node.Ref] = node.Reduce != "" || vectors.Count == 0 ? "scalar" : vectors[0];
                }
                catch (ArgumentException exception) { return exception.Message; }
            }
            else return "未知自定义查询节点";
        }

        if (definition.Outputs.Count is < 1 or > 20 || definition.Outputs.Distinct().Count() != definition.Outputs.Count
            || definition.Outputs.Any(x => !refs.Contains(x))) return "请选择有效输出";
        var outputs = definition.Outputs.Select(x => shapes[x]).ToList();
        if (outputs.Contains("table") && outputs.Count != 1) return "表格输出不能与其他输出混合";
        if (panelType is "line" or "area" && outputs.Any(x => x != "series")) return "时序图输出必须是时间序列";
        if (panelType == "stat" && outputs.Any(x => x != "scalar")) return "数值图输出必须归约为标量";
        if (panelType is "bar" or "pie" && outputs.Any(x => x is not ("scalar" or "category"))) return "分类图输出必须为标量或分类数值";
        return null;
    }

    private static bool ValidParameter(object? value) => value switch
    {
        null or string or bool or int or long or decimal => true,
        double number => double.IsFinite(number),
        System.Text.Json.JsonElement element => element.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
            || (element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetDouble(out var number) && double.IsFinite(number)),
        _ => false,
    };
}

/// <summary>SQL 输入或受限表达式节点，引用只能指向前序节点.</summary>
public class CustomPanelNode
{
    public string Ref { get; set; } = "A";
    public string? Alias { get; set; }
    public bool AutoGranularity { get; set; }
    public System.Text.Json.JsonElement? Editor { get; set; }
    public string Kind { get; set; } = "sql";
    public string Sql { get; set; } = "";
    public Dictionary<string, object?> Parameters { get; set; } = new();
    public string Shape { get; set; } = "scalar";
    public string ValueField { get; set; } = "value";
    public string TimeField { get; set; } = "";
    public List<string> GroupFields { get; set; } = new();
    public string Expression { get; set; } = "";
    public string Reduce { get; set; } = "";
}

/// <summary>与前端 formula.ts 一致的受限算术 AST，不执行用户代码.</summary>
public abstract record PanelExpression
{
    public abstract double? Evaluate(Func<string, double?> resolve);
    public abstract IEnumerable<string> References { get; }

    public static PanelExpression Parse(string expression, IEnumerable<string> references)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 512) throw new ArgumentException("表达式为空或过长");
        var tokens = Regex.Matches(expression, @"(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|[A-Z]+|[^\s]").Select(x => x.Value).ToArray();
        if (tokens.Length > 128) throw new ArgumentException("表达式过于复杂");
        var refs = references.ToHashSet(StringComparer.Ordinal);
        var position = 0;
        PanelExpression Primary(int depth)
        {
            if (depth > 24 || position >= tokens.Length) throw new ArgumentException("表达式语法错误");
            var token = tokens[position++];
            if (token is "+" or "-") return new Binary(token, new Number(0), Primary(depth + 1));
            if (token == "(")
            {
                var result = Sum(depth + 1);
                if (position >= tokens.Length || tokens[position++] != ")") throw new ArgumentException("表达式括号不匹配");
                return result;
            }
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return new Number(number);
            if (refs.Contains(token)) return new Reference(token);
            throw new ArgumentException("表达式包含未知引用或非法字符");
        }
        PanelExpression Product(int depth)
        {
            var result = Primary(depth);
            while (position < tokens.Length && tokens[position] is "*" or "/")
            {
                var op = tokens[position++];
                result = new Binary(op, result, Primary(depth));
            }
            return result;
        }
        PanelExpression Sum(int depth)
        {
            var result = Product(depth);
            while (position < tokens.Length && tokens[position] is "+" or "-")
            {
                var op = tokens[position++];
                result = new Binary(op, result, Product(depth));
            }
            return result;
        }
        var root = Sum(0);
        if (position != tokens.Length) throw new ArgumentException("表达式语法错误");
        return root;
    }

    private sealed record Number(double Value) : PanelExpression
    {
        public override IEnumerable<string> References => [];
        public override double? Evaluate(Func<string, double?> resolve) => Value;
    }
    private sealed record Reference(string Ref) : PanelExpression
    {
        public override IEnumerable<string> References => [Ref];
        public override double? Evaluate(Func<string, double?> resolve) => resolve(Ref);
    }
    private sealed record Binary(string Op, PanelExpression Left, PanelExpression Right) : PanelExpression
    {
        public override IEnumerable<string> References => Left.References.Concat(Right.References).Distinct();
        public override double? Evaluate(Func<string, double?> resolve)
        {
            var left = Left.Evaluate(resolve);
            var right = Right.Evaluate(resolve);
            if (!left.HasValue || !right.HasValue) return null;
            double? value = Op switch { "+" => left + right, "-" => left - right, "*" => left * right, "/" when right != 0 => left / right, _ => null };
            return value.HasValue && double.IsFinite(value.Value) ? value : null;
        }
    }
}
