namespace SData.Database.Entities;

/// <summary>只读 Agent 集成凭据，不存明文.</summary>
public class AgentKeyEntity : Audits.FullAuditedEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string ProtectedSecret { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
