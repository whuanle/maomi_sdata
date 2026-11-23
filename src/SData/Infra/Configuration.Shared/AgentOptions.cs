namespace SData.Infra.Configuration;

/// <summary>MCP 边界；并发上限由系统按有效核数自动调控（见 Store 的 ITuningState），预算只允许调整速率与超时.</summary>
public class AgentOptions
{
    public int RequestsPerMinute { get; set; } = 60;
    public int QueryTimeoutSeconds { get; set; } = 10;
}
