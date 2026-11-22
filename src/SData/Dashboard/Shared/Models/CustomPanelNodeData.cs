namespace SData.Dashboard;

/// <summary>设计器节点运行诊断，不进入保存配置.</summary>
public class CustomPanelNodeData
{
    public string Ref { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public List<List<object?>> Rows { get; set; } = new();
    public long ElapsedMs { get; set; }
    public bool Truncated { get; set; }
    public string? Error { get; set; }
}
