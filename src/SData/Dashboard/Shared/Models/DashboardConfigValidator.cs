using System.Text.Json;
using SData.Infra.Exceptions;

namespace SData.Dashboard;

/// <summary>
/// 保存配置校验：兼容旧协议，v2 坐标必须完整且在网格范围内.
/// </summary>
public static class DashboardConfigValidator
{
    /// <summary>校验持久化配置.</summary>
    public static void Validate(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw BusinessException.Throw(400, "仪表盘配置必须是 JSON 对象");
            }

            var config = JsonSerializer.Deserialize<DashboardConfig>(document.RootElement.GetRawText(), DashboardJson.Options)
                ?? throw BusinessException.Throw(400, "仪表盘配置不能为空");
            if (config.Version is not (1 or 2 or 3) || config.Panels == null || config.Panels.Count > 100)
            {
                throw BusinessException.Throw(400, "仪表盘版本不支持或组件数量超过 100");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<(PanelConfig Panel, bool Member)>(config.Panels.Select(panel => (panel, false)));
            while (queue.TryDequeue(out var entry))
            {
                var (panel, member) = entry;
                if (panel == null || string.IsNullOrWhiteSpace(panel.Id) || !ids.Add(panel.Id)
                    || !PanelTypes.All.Contains(panel.Type) || panel.Layout == null)
                {
                    throw BusinessException.Throw(400, "组件 ID 必须唯一且图表类型和布局有效");
                }

                if (ids.Count > 100) throw BusinessException.Throw(400, "组件和分组合计不能超过 100");
                if (panel.Type == PanelTypes.Group)
                {
                    if (config.Version != 3 || member || panel.Panels == null
                        || panel.Query != null || panel.Sql != null || panel.QueryId != 0
                        || (panel.DefaultPanelId != null && !panel.Panels.Any(child => child?.Id == panel.DefaultPanelId)))
                        throw BusinessException.Throw(400, "分组成员列表不能为空且不能嵌套，默认标签必须属于分组");
                    foreach (var child in panel.Panels) queue.Enqueue((child, true));
                }
                else if (panel.Panels != null || panel.DefaultPanelId != null)
                    throw BusinessException.Throw(400, "普通组件不能包含分组成员");

                ValidatePanelDefinition(panel);
                var layout = panel.Layout;
                if (layout.W is < 1 or > 12 || layout.H is < 1 or > 8
                    || (config.Version >= 2 && !member && (layout.X == null || layout.Y == null
                        || layout.X < 0 || layout.Y < 0 || layout.Y > 10000 || layout.X + layout.W > 12)))
                {
                    throw BusinessException.Throw(400, "组件布局超出网格范围");
                }

                if (panel.Markdown?.Length > PanelTypes.MarkdownMaxLength)
                {
                    throw BusinessException.Throw(400, "Markdown 正文不能超过 50000 个字符");
                }

                if (panel.Type != PanelTypes.Markdown && panel.Query != null && PanelQueryValidator.Validate(panel.Query) is { } error)
                {
                    throw BusinessException.Throw(400, error);
                }
            }
        }
        catch (JsonException)
        {
            throw BusinessException.Throw(400, "仪表盘配置必须是合法的 JSON");
        }
    }
    /// <summary>保存和预览共用展示、编辑及附属查询边界.</summary>
    public static void ValidatePanelDefinition(PanelConfig panel)
    {
        if (panel.Query?.Kind == PanelQueryKinds.Custom && CustomPanelDefinition.Validate(panel.Query.Custom, panel.Type) is { } customError)
        {
            throw BusinessException.Throw(400, customError);
        }

        ValidateEditor(panel.Editor);
        if (panel.Explorer is { ValueKind: JsonValueKind.Object } legacy && legacy.TryGetProperty("editor", out var editor))
        {
            ValidateEditor(editor);
        }

        if (panel.Visualization is { ValueKind: not JsonValueKind.Null } visual
            && (visual.ValueKind != JsonValueKind.Object || visual.GetRawText().Length > 200_000
                || !visual.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
                || !Allowed(visual, "kind", ["time_series", "number", "table", "bar", "pie", "heatmap", "search", "patterns"])
                || !Allowed(visual, "display", ["line", "bar"]) || !ValidOutputs(visual)
                || !visual.TryGetProperty("range", out var range) || range.ValueKind != JsonValueKind.Object
                || !range.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.String || !from.TryGetDateTimeOffset(out var start)
                || !range.TryGetProperty("to", out var to) || to.ValueKind != JsonValueKind.String || !to.TryGetDateTimeOffset(out var end) || start >= end))
        {
            throw BusinessException.Throw(400, "面板展示定义版本、类型或大小无效");
        }

        if (panel.Query?.NumberTrend is { } trend
            && (panel.Query.Kind != PanelQueryKinds.Sql || string.IsNullOrWhiteSpace(trend.Sql) || trend.Sql.Length > 100_000
                || trend.Parameters == null || trend.Parameters.Count > 64))
        {
            throw BusinessException.Throw(400, "数值趋势查询配置无效");
        }
    }

    /// <summary>供模型验证返回可读错误.</summary>
    public static string? PanelDefinitionError(PanelConfig panel)
    {
        try { ValidatePanelDefinition(panel); return null; }
        catch (BusinessException exception) { return exception.Message; }
    }

    private static void ValidateEditor(JsonElement? definition)
    {
        if (definition is null || definition.Value.ValueKind == JsonValueKind.Null) return;
        var editor = definition.Value;

        if (editor.ValueKind != JsonValueKind.Object || editor.GetRawText().Length > 200_000
            || !editor.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
            || !Allowed(editor, "mode", ["builder", "sql"])
            || !Allowed(editor, "source", ["logs", "traces", "metrics", "sessions", "iot"])
            || !Allowed(editor, "chartType", ["time_series", "number", "table", "bar", "pie", "heatmap", "search", "patterns"])
            || !Allowed(editor, "granularity", ["auto", "1m", "5m", "15m", "30m", "1h", "6h", "1d"])
            || !BoundedArray(editor, "series", 12) || !BoundedArray(editor, "formulas", 8) || !BoundedArray(editor, "groupBy", 20))
        {
            throw BusinessException.Throw(400, "图表编辑定义版本、类型或大小无效");
        }
    }

    private static bool ValidOutputs(JsonElement visual)
    {
        if (!BoundedArray(visual, "outputs", 20)) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in visual.GetProperty("outputs").EnumerateArray())
        {
            if (output.ValueKind != JsonValueKind.Object
                || !output.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 128 || !ids.Add(id.GetString()!)
                || !output.TryGetProperty("alias", out var alias) || alias.ValueKind != JsonValueKind.String || alias.GetString()!.Length > 512)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Allowed(JsonElement editor, string name, string[] values)
        => editor.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && values.Contains(value.GetString());

    private static bool BoundedArray(JsonElement editor, string name, int maximum)
        => editor.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maximum;

}
