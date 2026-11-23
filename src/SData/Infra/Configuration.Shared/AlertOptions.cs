namespace SData.Infra.Configuration;

/// <summary>低并发告警预算；所有告警与预览共享同一执行槽（待投递容量由系统按环境内存自动调控）.</summary>
public class AlertOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxEnabledRules { get; set; } = 50;
    public int QueryTimeoutSeconds { get; set; } = 5;
    public int BudgetSecondsPerMinute { get; set; } = 15;
}
