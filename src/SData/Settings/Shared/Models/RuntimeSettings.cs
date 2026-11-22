using SData.Infra.Configuration;

namespace SData.Settings;

/// <summary>
/// 运行参数（设置页「运行参数」；保存后各消费端下一次读取即生效，未保存时回退文件/环境变量默认）.
/// </summary>
public class RuntimeSettings
{
    /// <summary>搜索告警执行预算.</summary>
    public AlertOptions Alerts { get; set; } = new();

    /// <summary>MCP 访问预算.</summary>
    public AgentOptions Agents { get; set; } = new();

    /// <summary>DuckDB 查询与并发上限（内存限制见 DuckDB 页签）.</summary>
    public DuckDbTuningOptions DuckDb { get; set; } = new();

    /// <summary>冷数据本地缓存.</summary>
    public ColdCacheOptions ColdCache { get; set; } = new();

    /// <summary>登录 token 有效期（仅影响之后签发的 token）.</summary>
    public AuthTuningOptions Auth { get; set; } = new();

    /// <summary>摄取批量落库参数.</summary>
    public IngestTuningOptions Ingest { get; set; } = new();
}

/// <summary>
/// DuckDB 可调查询与并发参数（不含路径、临时目录与内存限制）.
/// </summary>
public class DuckDbTuningOptions
{
    /// <summary>线程数（SET threads，实例级全局设置，立即生效）.</summary>
    public int Threads { get; set; } = 2;

    /// <summary>同时执行的查询数（立即生效，降低小内存部署的查询竞争）.</summary>
    public int MaxConcurrentQueries { get; set; } = 1;

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

/// <summary>
/// 摄取批量落库配置（写入器每轮读取，立即生效；缓冲容量为启动参数，见 appsettings.json）.
/// </summary>
public class IngestTuningOptions
{
    /// <summary>批量落库条数.</summary>
    public int BatchSize { get; set; } = 5_000;

    /// <summary>落库间隔（毫秒）.</summary>
    public int FlushIntervalMs { get; set; } = 200;
}
