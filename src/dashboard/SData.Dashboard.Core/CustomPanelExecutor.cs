using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SData.Store;

namespace SData.Dashboard;

/// <summary>自定义查询图执行器；SQL 仅通过统一分析执行器，表达式只处理有界内存结果.</summary>
public sealed class CustomPanelExecutor(IAnalyticsExecutor analytics)
{
    private sealed record Point(long? Time, string Group, double? Value);
    private sealed record Frame(string Shape, List<string> Groups, List<Point> Points, List<string> Columns, List<List<object?>> Rows);

    public async Task ExecuteAsync(CustomPanelDefinition definition, PanelData data, int limit, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (CustomPanelDefinition.Validate(definition) is { } error) throw new ArgumentException(error);
        var frames = new Dictionary<string, Frame>(StringComparer.Ordinal);
        var budget = 0;
        foreach (var node in definition.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            var diagnostic = new CustomPanelNodeData { Ref = node.Ref };
            data.Nodes.Add(diagnostic);
            try
            {
                Frame frame;
                if (node.Kind == "sql")
                {
                    var parameters = new Dictionary<string, object?>(node.Parameters);
                    if (node.AutoGranularity)
                    {
                        var hours = (to - from).TotalHours;
                        parameters["chart_bucket"] = hours <= 1 ? "1 minute" : hours <= 6 ? "5 minutes"
                            : hours <= 24 ? "15 minutes" : hours <= 48 ? "30 minutes" : hours <= 168 ? "1 hour" : "1 day";
                    }

                    var result = await analytics.ExecuteSearchAsync(node.Sql, from, to, Math.Clamp(limit, 1, 10_000), parameters, cancellationToken);
                    diagnostic.Columns = result.Columns;
                    diagnostic.Rows = result.Rows.Take(30).ToList();
                    diagnostic.Truncated = result.Truncated;
                    data.Truncated |= result.Truncated;
                    if (result.Columns.Count > 200 || result.Rows.Sum(x => x.Count) + budget > 200_000) throw new ArgumentException("自定义查询超过单面板结果预算");
                    budget += result.Rows.Sum(x => x.Count);
                    frame = Map(node, result.Columns, result.Rows);
                }
                else
                {
                    var expression = PanelExpression.Parse(node.Expression, frames.Keys);
                    var inputs = expression.References.Select(x => frames[x]).ToList();
                    if (inputs.Any(x => x.Shape == "table")) throw new ArgumentException("表格输入需先映射为标量或时间序列");
                    var vectors = inputs.Where(x => x.Shape is "series" or "category").ToList();
                    if (vectors.Any(x => x.Shape != vectors[0].Shape || !x.Groups.SequenceEqual(vectors[0].Groups))) throw new ArgumentException("表达式时间序列的分组字段必须一致");
                    var maps = expression.References.ToDictionary(x => x, x => frames[x].Points.ToDictionary(p => (p.Time, p.Group)));
                    var keys = vectors.SelectMany(x => x.Points.Select(p => (p.Time, p.Group))).Distinct().ToList();
                    if (vectors.Count == 0) keys.Add((null, ""));
                    if (keys.Count + budget > 200_000) throw new ArgumentException("表达式超过单面板结果预算");
                    budget += keys.Count;
                    var points = keys.Select(key => new Point(key.Time, key.Group, expression.Evaluate(reference =>
                    {
                        var input = frames[reference];
                        return input.Shape == "scalar" ? input.Points.FirstOrDefault()?.Value : maps[reference].GetValueOrDefault(key)?.Value;
                    }))).ToList();
                    if (node.Reduce != "")
                    {
                        var values = points.OrderBy(x => x.Time).Select(x => x.Value).Where(x => x.HasValue).Select(x => x!.Value).ToList();
                        double? value = node.Reduce == "count" ? values.Count : values.Count == 0 ? null : node.Reduce switch
                        {
                            "sum" => values.Sum(), "avg" => values.Average(), "min" => values.Min(), "max" => values.Max(), "last" => values[^1], _ => null,
                        };
                        points = [new(null, "", value.HasValue && double.IsFinite(value.Value) ? value : null)];
                    }
                    frame = new Frame(vectors.Count == 0 || node.Reduce != "" ? "scalar" : vectors[0].Shape, node.Reduce != "" ? [] : vectors.FirstOrDefault()?.Groups ?? [], points, [], []);
                    diagnostic.Columns = ["time", "group", "value"];
                    diagnostic.Rows = points.Take(30).Select(x => new List<object?> { x.Time, x.Group, x.Value }).ToList();
                }
                frames[node.Ref] = frame;
            }
            catch (OperationCanceledException) { throw; }
            catch (SData.Infra.Exceptions.BusinessException exception)
            {
                diagnostic.Error = exception.Message;
                throw new ArgumentException($"节点 {node.Ref}：{exception.Message}", exception);
            }
            catch (ArgumentException exception)
            {
                diagnostic.Error = exception.Message;
                throw new ArgumentException($"节点 {node.Ref}：{exception.Message}", exception);
            }
            finally { diagnostic.ElapsedMs = watch.ElapsedMilliseconds; }
        }

        var outputs = definition.Outputs.Select(x => (Ref: x, Frame: frames[x])).ToList();
        if (outputs.Any(x => x.Frame.Shape == "table"))
        {
            if (outputs.Count != 1) throw new ArgumentException("表格输出不能与其他输出混合");
            data.Columns = outputs[0].Frame.Columns;
            data.Rows = outputs[0].Frame.Rows;
            return;
        }
        data.Columns = ["ts_bucket", "series", "value", "__series_id", "category"];
        foreach (var output in outputs)
        {
            foreach (var point in output.Frame.Points)
            {
                if (data.Rows.Count >= 10_000) { data.Truncated = true; return; }
                data.Rows.Add([point.Time.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(point.Time.Value).ToString("O") : null,
                    output.Ref + (point.Group == "" ? "" : " " + point.Group), point.Value, output.Ref + (point.Group.Length == 0 ? string.Empty : ":" + point.Group), output.Ref + (point.Group == "" ? "" : " " + point.Group)]);
            }
        }
    }

    private static Frame Map(CustomPanelNode node, List<string> columns, List<List<object?>> rows)
    {
        if (node.Shape == "table") return new("table", [], [], columns, rows);
        int Field(string name)
        {
            var index = columns.IndexOf(name);
            if (index < 0 || columns.Count(x => x == name) != 1) throw new ArgumentException($"字段不存在或不唯一：{name}");
            return index;
        }
        var valueIndex = Field(node.ValueField);
        var timeIndex = node.Shape == "series" ? Field(node.TimeField) : -1;
        var groups = node.GroupFields.Order(StringComparer.Ordinal).ToList();
        var groupIndices = groups.Select(Field).ToList();
        if (node.Shape == "scalar" && rows.Count > 1) throw new ArgumentException("标量查询只能返回一行，请在 SQL 中聚合");
        var points = new List<Point>();
        var keys = new HashSet<(long?, string)>();
        foreach (var row in rows)
        {
            var raw = row[valueIndex];
            double? value = null;
            if (raw != null)
            {
                if (!double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) throw new ArgumentException("数值字段必须为有限数字或 null");
                value = number;
            }
            long? time = null;
            if (timeIndex >= 0)
            {
                var rawTime = row[timeIndex];
                time = rawTime switch
                {
                    DateTimeOffset dto => dto.ToUnixTimeMilliseconds(),
                    DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                    long milliseconds => milliseconds,
                    _ => DateTimeOffset.TryParse(Convert.ToString(rawTime, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUnixTimeMilliseconds() : throw new ArgumentException("时间字段必须是时间戳或毫秒整数"),
                };
            }
            var group = groups.Count == 0 ? "" : JsonSerializer.Serialize(groupIndices.Select(x => row[x]).ToArray());
            if (!keys.Add((time, group))) throw new ArgumentException("查询结果存在重复时间与分组键，请先聚合");
            points.Add(new(time, group, value));
        }
        return new(node.Shape, groups, points, columns, rows);
    }
}
