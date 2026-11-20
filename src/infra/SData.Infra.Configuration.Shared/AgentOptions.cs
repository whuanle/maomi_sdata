namespace SData.Infra.Configuration;

/// <summary>MCP 边界；预算只允许在硬上限以内调整.</summary>
public class AgentOptions
{
    public int RequestsPerMinute { get; set; } = 60;
    public int PerKeyConcurrency { get; set; } = 2;
    public int GlobalConcurrency { get; set; } = 8;
    public int QueryTimeoutSeconds { get; set; } = 10;
    /// <summary>额外可信 Host，不含端口；默认允许 Server 主机名及 loopback.</summary>
    public List<string> AllowedHosts { get; set; } = new();
}
