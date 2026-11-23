using SData.Infra.Configuration;

namespace SData.Settings;

/// <summary>
/// 运行参数（设置页「运行参数」；保存后各消费端下一次读取即生效，未保存时回退文件/环境变量默认）.
/// 性能参数（缓冲、批量、DuckDB 线程/并发/内存等）不在此列，由系统按环境自动调控（见 Store 的 ITuningState）.
/// </summary>
public class RuntimeSettings
{
    /// <summary>搜索告警执行预算.</summary>
    public AlertOptions Alerts { get; set; } = new();

    /// <summary>MCP 访问预算（速率与超时；并发上限由系统自动调控）.</summary>
    public AgentOptions Agents { get; set; } = new();

    /// <summary>DuckDB 查询策略（超时与跨度上限；线程/并发/内存由系统自动调控）.</summary>
    public DuckDbTuningOptions DuckDb { get; set; } = new();

    /// <summary>冷数据本地缓存.</summary>
    public ColdCacheOptions ColdCache { get; set; } = new();

    /// <summary>登录 token 有效期（仅影响之后签发的 token）.</summary>
    public AuthTuningOptions Auth { get; set; } = new();
}

/// <summary>
/// DuckDB 查询策略参数（超时/跨度；线程、并发、内存限制由系统自动调控）.
/// </summary>
public class DuckDbTuningOptions
{
    /// <summary>分析查询预算（秒）.</summary>
    public int QueryTimeoutSeconds { get; set; } = 30;

    /// <summary>单次查询允许的最大时间跨度（天）.</summary>
    public int MaxQueryDays { get; set; } = 31;

    /// <summary>含冷层（归档对象）查询允许的最大时间跨度（天）.</summary>
    public int ColdMaxQueryDays { get; set; } = 366;
}

/// <summary>
/// 登录 token 有效期配置.
/// </summary>
public class AuthTuningOptions
{
    /// <summary>token 有效期（分钟）.</summary>
    public int TokenExpireMinutes { get; set; } = 720;
}
