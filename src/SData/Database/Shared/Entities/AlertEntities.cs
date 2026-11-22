namespace SData.Database.Entities;

/// <summary>搜索告警规则及持久运行状态.</summary>
public class AlertRuleEntity : Audits.FullAuditedEntity
{
    public long Id { get; set; }
    public long SavedQueryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Domain { get; set; } = "logs";
    public string Definition { get; set; } = "{}";
    public string Condition { get; set; } = string.Empty;
    public string Parameters { get; set; } = "{}";
    public string Channels { get; set; } = "[]";
    public string Note { get; set; } = string.Empty;
    public string Operator { get; set; } = "gte";
    public long Threshold { get; set; } = 1;
    public int WindowMinutes { get; set; } = 5;
    public int DelayMinutes { get; set; } = 1;
    public int ConsecutiveWindows { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public long Version { get; set; } = 1;
    public string Status { get; set; } = "pending";
    public bool Firing { get; set; }
    public int Hits { get; set; }
    public int Failures { get; set; }
    public long LastWindow { get; set; }
    public long NextAttempt { get; set; }
    public string Error { get; set; } = string.Empty;
}

/// <summary>有限窗口评估历史，不存日志明细.</summary>
public class AlertEvaluationEntity : Audits.FullAuditedEntity
{
    public long Id { get; set; }
    public long RuleId { get; set; }
    public long Version { get; set; }
    public long WindowEnd { get; set; }
    public long Count { get; set; }
    public bool LowerBound { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

/// <summary>有界通知 outbox；外部投递语义为至少一次.</summary>
public class AlertDeliveryEntity : Audits.FullAuditedEntity
{
    public long Id { get; set; }
    public long RuleId { get; set; }
    public long Version { get; set; }
    public long WindowEnd { get; set; }
    public long ChannelId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public long NextAttempt { get; set; }
    public string Status { get; set; } = "pending";
    public string Result { get; set; } = string.Empty;
}
