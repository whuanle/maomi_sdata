using System.Text.Json;

namespace SData.Dashboard;

/// <summary>节点编辑恢复元数据的有界校验；执行器不解释这些元数据.</summary>
public static class CustomNodeEditorValidator
{
    public static bool IsValid(JsonElement? definition, string kind)
    {
        if (definition is null || definition.Value.ValueKind == JsonValueKind.Null) return true;
        var editor = definition.Value;
        if (editor.ValueKind != JsonValueKind.Object || editor.GetRawText().Length > 30_000
            || !editor.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
            || !Allowed(editor, "mode", kind == "sql" ? ["builder"] : ["guided"])
            || !Allowed(editor, "source", ["logs", "traces", "metrics", "sessions", "iot"])
            || !Allowed(editor, "shape", ["table", "scalar", "series", "category"])
            || !Allowed(editor, "aggregation", ["count", "avg", "sum", "min", "max", "p50", "p90", "p95", "p99", "count_distinct"])
            || !Allowed(editor, "granularity", ["auto", "1m", "5m", "15m", "30m", "1h", "6h", "1d"])
            || !Text(editor, "field", 256) || !Text(editor, "where", 4000)
            || !editor.TryGetProperty("groupBy", out var groups) || groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() > 20
            || groups.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || x.GetString()!.Length > 256))
        {
            return false;
        }

        if (kind == "expression" && (!Allowed(editor, "operation", ["sum", "difference", "ratio", "percentage", "reduce"])
            || !Text(editor, "left", 3) || !Text(editor, "right", 3))) return false;
        if (editor.TryGetProperty("metricName", out var metric) && metric.ValueKind != JsonValueKind.Null
            && (metric.ValueKind != JsonValueKind.String || metric.GetString()!.Length > 512)) return false;
        if (editor.TryGetProperty("metricFilters", out var filters)
            && (filters.ValueKind != JsonValueKind.Array || filters.GetArrayLength() > 20
                || filters.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Object || !Text(x, "field", 256) || !Text(x, "value", 4000)))) return false;
        return true;
    }

    private static bool Allowed(JsonElement editor, string name, string[] values)
        => editor.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && values.Contains(value.GetString());

    private static bool Text(JsonElement editor, string name, int limit)
        => editor.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= limit;
}
