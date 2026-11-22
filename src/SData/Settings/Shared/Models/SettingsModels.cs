using SData.Infra.Configuration;

namespace SData.Settings;

/// <summary>
/// 系统设置响应（GET /api/settings；SecretKey 掩码返回）.
/// </summary>
public class SettingsResponse
{
    /// <summary>S3 冷存储（SecretKey 掩码）.</summary>
    public S3SettingsModel S3 { get; set; } = new();

    /// <summary>DuckDB 内存配置与当前生效值.</summary>
    public DuckDbSettingsResponse DuckDb { get; set; } = new();

    /// <summary>三段保留（5 域热/冷天数）.</summary>
    public RetentionPlanOptions Retention { get; set; } = new();

    /// <summary>数据压缩.</summary>
    public CompressionOptions Compression { get; set; } = new();

    /// <summary>自身遥测导出（重启生效）.</summary>
    public OtlpOptions Otlp { get; set; } = new();

    /// <summary>运行参数（合并视图；保存见 PUT /settings/runtime）.</summary>
    public RuntimeSettings Runtime { get; set; } = new();

    /// <summary>摄取启动参数（启动时绑定，只读展示；批量参数见 Runtime.Ingest）.</summary>
    public IngestSettingsInfo Ingest { get; set; } = new();

    /// <summary>启动参数与 configs/system.json 覆盖状态（只读；「启动配置」页签）.</summary>
    public StartupSettingsInfo Startup { get; set; } = new();
}

/// <summary>
/// 启动参数只读快照（白名单字段，不含 InitialAdminPassword/InitialApiKey 等敏感项；可在 configs/system.json 覆盖，重启生效）.
/// </summary>
public class StartupSettingsInfo
{
    /// <summary>configs/system.json 是否已创建（未创建 = 全部使用代码默认值）.</summary>
    public bool SystemJsonExists { get; set; }

    /// <summary>HTTP 端口（SData:Port）.</summary>
    public int Port { get; set; }

    /// <summary>MQTT 监听端口（SData:Mqtt:Port）.</summary>
    public int MqttPort { get; set; }

    /// <summary>是否托管前端静态页面.</summary>
    public bool WebUI { get; set; }

    /// <summary>SQLite 元数据库路径.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>DuckDB 分析库路径.</summary>
    public string DuckDbPath { get; set; } = string.Empty;

    /// <summary>DuckDB 内存上限.</summary>
    public string DuckDbMemoryLimit { get; set; } = string.Empty;

    /// <summary>DuckDB WAL checkpoint 阈值.</summary>
    public string DuckDbCheckpointThreshold { get; set; } = string.Empty;

    /// <summary>DuckDB 查询结果缓存 TTL（秒，0 = 关闭）.</summary>
    public int DuckDbQueryCacheTtlSeconds { get; set; }

    /// <summary>DuckDB 查询结果缓存最大条目数.</summary>
    public int DuckDbQueryCacheMaxEntries { get; set; }

    /// <summary>DuckDB 临时目录（空 = 系统默认）.</summary>
    public string? DuckDbTempDirectory { get; set; }

    /// <summary>接入端点是否要求鉴权.</summary>
    public bool IngestRequireAuth { get; set; }

    /// <summary>内存缓冲容量（条；启动时固定，修改需重启）.</summary>
    public int IngestBufferCapacity { get; set; }

    /// <summary>内存缓冲估算字节预算（启动时固定，修改需重启）.</summary>
    public long IngestBufferByteBudgetBytes { get; set; }
}

/// <summary>
/// 摄取启动参数只读快照（白名单字段，不含 InitialApiKey 等敏感项）.
/// </summary>
public class IngestSettingsInfo
{
    /// <summary>接入端点是否要求鉴权.</summary>
    public bool RequireAuth { get; set; }

    /// <summary>内存缓冲容量（条；有界 Channel 构造期固定，修改需重启）.</summary>
    public int BufferCapacity { get; set; }

    /// <summary>内存缓冲估算字节预算（启动配置固定，修改需重启）.</summary>
    public long BufferByteBudgetBytes { get; set; }
}

/// <summary>
/// S3 冷存储设置项.
/// </summary>
public class S3SettingsModel
{
    /// <summary>SecretKey 掩码常量（GET 返回；保存/测试时收到该值 = 沿用已存密钥）.</summary>
    public const string SecretMask = "******";

    /// <summary>S3 兼容端点（空 = 关闭冷层）.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>存储桶.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>对象键前缀.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Access Key.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Secret Key（GET 掩码；空或掩码 = 沿用已存值）.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>是否已配置 SecretKey.</summary>
    public bool HasSecret { get; set; }
}

/// <summary>
/// S3 连接测试响应.
/// </summary>
public class TestS3ConnectionResponse
{
    /// <summary>是否连通.</summary>
    public bool Success { get; set; }

    /// <summary>结果消息（人类可读中文）.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// MQ 日志解析预览单条结果（样例文本按解析规则解析后的映射效果）.
/// </summary>
public class MqLogPreviewRecord
{
    /// <summary>时间戳（ISO 8601 UTC）.</summary>
    public string Timestamp { get; set; } = string.Empty;

    /// <summary>严重级别（归一化后）.</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>服务名.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>日志正文.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>trace id.</summary>
    public string? TraceId { get; set; }

    /// <summary>span id.</summary>
    public string? SpanId { get; set; }

    /// <summary>附加属性 JSON（未映射字段 / _parse_error 标记）.</summary>
    public string? AttributesJson { get; set; }
}

/// <summary>
/// MQ 日志解析预览结果（不落库，仅配置调试）.
/// </summary>
public class MqParsePreviewResult
{
    /// <summary>是否全部解析成功（存在 _parse_error 标记行时为 false）.</summary>
    public bool Success { get; set; }

    /// <summary>失败原因（解析完全失败时的人类可读中文；部分失败看各记录 attributes）.</summary>
    public string? Error { get; set; }

    /// <summary>解析结果（含失败行的原文入库结果）.</summary>
    public List<MqLogPreviewRecord> Records { get; set; } = new();
}
