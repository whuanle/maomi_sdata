namespace SData.Infra.Configuration;

/// <summary>
/// 系统配置（SData 节）：字段均有内置默认值，启动参数可用环境变量 SData__* 覆盖，业务设置保存在设置页.
/// </summary>
public class SystemOptions
{
    /// <summary>
    /// 调试模式.
    /// </summary>
    public bool Debug { get; set; }

    /// <summary>搜索告警执行预算.</summary>
    public AlertOptions Alerts { get; set; } = new();

    /// <summary>MCP 访问预算.</summary>
    public AgentOptions Agents { get; set; } = new();

    /// <summary>
    /// 服务名.
    /// </summary>
    public string Name { get; set; } = "sdata";

    /// <summary>
    /// 监听端口.
    /// </summary>
    public int Port { get; set; } = 5300;

    /// <summary>
    /// 是否托管前端静态页面（wwwroot）.
    /// </summary>
    public bool WebUI { get; set; } = true;

    /// <summary>
    /// 元数据库（SQLite）.
    /// </summary>
    public DatabaseOptions Database { get; set; } = new();

    /// <summary>
    /// 分析库（DuckDB）.
    /// </summary>
    public DuckDbOptions DuckDb { get; set; } = new();

    /// <summary>
    /// 认证配置.
    /// </summary>
    public AuthOptions Auth { get; set; } = new();

    /// <summary>
    /// 数据接入配置.
    /// </summary>
    public IngestOptions Ingest { get; set; } = new();

    /// <summary>
    /// 自身遥测导出（OTLP）.
    /// </summary>
    public OtlpOptions OTLP { get; set; } = new();

    /// <summary>
    /// S3 冷存储（契约 C4；Endpoint 为空 = 关闭冷层，连接初始化完全跳过）.
    /// </summary>
    public S3Options S3 { get; set; } = new();

    /// <summary>
    /// 冷数据本地缓存（S3 归档对象后台预热，查询路径零阻塞；未配置 S3 时不生效）.
    /// </summary>
    public ColdCacheOptions ColdCache { get; set; } = new();

    /// <summary>
    /// 三段保留（契约 C4/T512）：SData:Retention:{Domain}:{HotDays,ColdDays}；旧形态 DuckDb.Retention:{X} 兼容映射为 HotDays.
    /// </summary>
    public RetentionPlanOptions Retention { get; set; } = new();

    /// <summary>
    /// 数据压缩（冷存档 parquet 列压缩编码）.
    /// </summary>
    public CompressionOptions Compression { get; set; } = new();

    /// <summary>
    /// Kafka 日志消费（可选输入通道；Enabled=false 或未配置时不启动消费者，不建任何连接）.
    /// </summary>
    public KafkaOptions Kafka { get; set; } = new();

    /// <summary>
    /// RabbitMQ 日志消费（可选输入通道；Enabled=false 或未配置时不启动消费者，不建任何连接）.
    /// </summary>
    public RabbitMqOptions RabbitMq { get; set; } = new();

    /// <summary>
    /// Apache Pulsar 日志消费（可选输入通道；Enabled=false 或未配置时不启动消费者，不建任何连接）.
    /// </summary>
    public PulsarOptions Pulsar { get; set; } = new();

    /// <summary>
    /// MQ 日志消费实例列表（多实例形态，文件回退用；运行时优先读设置键 system:mq，未保存时回退 Kafka/RabbitMq/Pulsar 三节合成）.
    /// </summary>
    public List<MqInstanceOptions> Mq { get; set; } = new();
}

/// <summary>
/// Kafka 日志消费配置.
/// </summary>
public class KafkaOptions
{
    /// <summary>是否启用.</summary>
    public bool Enabled { get; set; }

    /// <summary>broker 地址列表（如 broker1:9092,broker2:9092；空 = 关闭）.</summary>
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>消费组 id.</summary>
    public string GroupId { get; set; } = "sdata-logs";

    /// <summary>无已提交 offset 时的起始位置：earliest / latest.</summary>
    public string AutoOffsetReset { get; set; } = "earliest";

    /// <summary>消费的 topic 列表（每项可配置消息格式）.</summary>
    public List<MqLogSourceOptions> Topics { get; set; } = new();
}

/// <summary>
/// RabbitMQ 日志消费配置.
/// </summary>
public class RabbitMqOptions
{
    /// <summary>是否启用.</summary>
    public bool Enabled { get; set; }

    /// <summary>AMQP 连接串（amqp://user:pass@host:5672/vhost；空 = 关闭）.</summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>消费的队列列表（每项可配置消息格式；队列不存在时按 durable 队列幂等声明）.</summary>
    public List<MqLogSourceOptions> Queues { get; set; } = new();
}

/// <summary>
/// Apache Pulsar 日志消费配置.
/// </summary>
public class PulsarOptions
{
    /// <summary>是否启用.</summary>
    public bool Enabled { get; set; }

    /// <summary>Pulsar 服务地址（pulsar://host:6650；空 = 关闭）.</summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>订阅名.</summary>
    public string SubscriptionName { get; set; } = "sdata-logs";

    /// <summary>订阅类型：Shared / Failover / Exclusive（多 topic 建议 Shared）.</summary>
    public string SubscriptionType { get; set; } = "Shared";

    /// <summary>消费的 topic 列表（完整路径，如 persistent://public/default/logs）.</summary>
    public List<MqLogSourceOptions> Topics { get; set; } = new();
}

/// <summary>
/// MQ 日志来源（topic/queue）配置.
/// </summary>
public class MqLogSourceOptions
{
    /// <summary>topic / 队列名.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>消息格式：otlp-proto / otlp-json / json / raw.</summary>
    public string Format { get; set; } = "otlp-json";

    /// <summary>默认服务名（消息本身不含服务名时使用；json/raw 格式生效）.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>默认严重级别（raw 格式使用，缺省 INFO）.</summary>
    public string Severity { get; set; } = "INFO";
}

/// <summary>
/// 三段保留计划（按域）.
/// </summary>
public class RetentionPlanOptions
{
    /// <summary>日志保留（热/冷）.</summary>
    public RetentionTierOptions Logs { get; set; } = new() { HotDays = 30, ColdDays = 90 };

    /// <summary>链路保留（热/冷）.</summary>
    public RetentionTierOptions Traces { get; set; } = new() { HotDays = 14, ColdDays = 60 };

    /// <summary>指标保留（热/冷）.</summary>
    public RetentionTierOptions Metrics { get; set; } = new() { HotDays = 90, ColdDays = 180 };

    /// <summary>遥测保留（热/冷）.</summary>
    public RetentionTierOptions Iot { get; set; } = new() { HotDays = 365, ColdDays = 0 };

    /// <summary>RUM 保留（热/冷）.</summary>
    public RetentionTierOptions Rum { get; set; } = new() { HotDays = 30, ColdDays = 90 };
}

/// <summary>
/// 单域保留档位.
/// </summary>
public class RetentionTierOptions
{
    /// <summary>热存天数（&lt;=0 时回退旧形态 DuckDb.Retention:{X}）.</summary>
    public int HotDays { get; set; }

    /// <summary>冷存天数（0 = 不转冷，热到期直接过期删除）.</summary>
    public int ColdDays { get; set; }
}

/// <summary>
/// 数据压缩配置（冷存档 parquet 列压缩编码；DuckDB COPY 支持 uncompressed/snappy/gzip/zstd）.
/// </summary>
public class CompressionOptions
{
    /// <summary>支持的压缩编码白名单（DuckDB COPY parquet）.</summary>
    public static readonly string[] SupportedCodecs = ["uncompressed", "snappy", "gzip", "zstd"];

    /// <summary>冷存档 parquet 压缩编码（默认 snappy，与 DuckDB 缺省一致）.</summary>
    public string ArchiveParquetCodec { get; set; } = "snappy";
}

/// <summary>
/// S3 冷存储配置（契约 C4；Endpoint 为空 = 关闭冷层）.
/// </summary>
public class S3Options
{
    /// <summary>S3 兼容端点（如 http://minio:9000；空 = 关闭冷层）.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>存储桶.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>对象键前缀（如 sdata/archive）.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>
    /// 归档分片粒度：day（默认，一天一个对象+一个索引）或 hour（一天按小时分片为多对象+多索引，
    /// 冷查询候选裁剪更细、只拉命中分片；未知值回退 day）.
    /// </summary>
    public string ArchiveGranularity { get; set; } = "day";

    /// <summary>Access Key.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Secret Key.</summary>
    public string SecretKey { get; set; } = string.Empty;
}

/// <summary>
/// 冷数据本地缓存配置（S3 归档对象预热到本地磁盘，LRU 容量淘汰）.
/// </summary>
public class ColdCacheOptions
{
    /// <summary>是否启用（false = 不预热不缓存，S3 恒直读）.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>缓存目录容量上限（字节），超限按最久未访问淘汰.</summary>
    public long MaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}

/// <summary>
/// SQLite 元数据库配置.
/// </summary>
public class DatabaseOptions
{
    /// <summary>
    /// sqlite 文件路径.
    /// </summary>
    public string Path { get; set; } = "data/sdata.db";
}

/// <summary>
/// DuckDB 分析库配置（内网单机小内存部署，默认限制内存与线程）.
/// </summary>
public class DuckDbOptions
{
    /// <summary>
    /// duckdb 文件路径.
    /// </summary>
    public string Path { get; set; } = "data/sdata.duckdb";

    /// <summary>
    /// 内存上限（DuckDB memory_limit，如 512MB）.
    /// </summary>
    public string MemoryLimit { get; set; } = "2GB";

    /// <summary>
    /// 线程数，默认 8，0 = 自动.
    /// </summary>
    public int Threads { get; set; } = 8;

    /// <summary>
    /// 同时执行的查询数（1–32），默认 4；小内存部署可调回 1 降低查询资源竞争.
    /// </summary>
    public int MaxConcurrentQueries { get; set; } = 4;

    /// <summary>
    /// WAL checkpoint 阈值（DuckDB checkpoint_threshold，如 128MB）；WAL 超过该值在提交时触发 checkpoint.
    /// 调大可显著降低高频提交时 checkpoint 抢占 commit 的概率；空 = 使用引擎默认.
    /// </summary>
    public string CheckpointThreshold { get; set; } = "1GB";

    /// <summary>
    /// DSL 查询结果缓存 TTL（秒，0 = 关闭）。range 尾端按 TTL 对齐时间桶，仪表盘轮询在 TTL 内命中缓存；最坏陈旧度 = TTL.
    /// </summary>
    public int QueryCacheTtlSeconds { get; set; } = 5;

    /// <summary>
    /// DSL 查询结果缓存最大条目数.
    /// </summary>
    public int QueryCacheMaxEntries { get; set; } = 256;

    /// <summary>
    /// 分析查询（{{logs}} 占位符 SQL）结果缓存 TTL（秒，0 = 关闭）。窗口尾端按 TTL 对齐时间桶，仪表盘重复轮询在 TTL 内命中缓存；最坏陈旧度 = TTL.
    /// </summary>
    public int AnalyticsQueryCacheTtlSeconds { get; set; } = 5;

    /// <summary>
    /// 分析查询结果缓存最大条目数.
    /// </summary>
    public int AnalyticsQueryCacheMaxEntries { get; set; } = 256;

    /// <summary>分析查询预算（秒，1–300），包含准入等待及原生执行.</summary>
    public int QueryTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// 临时目录（溢出用），空则使用系统默认.
    /// </summary>
    public string? TempDirectory { get; set; }

    /// <summary>
    /// 单次查询允许的最大时间跨度（天），防止全表扫.
    /// </summary>
    public int MaxQueryDays { get; set; } = 31;

    /// <summary>
    /// 含冷层（归档对象）查询允许的最大时间跨度（天）；与 MaxQueryDays 取较大值生效.
    /// </summary>
    public int ColdMaxQueryDays { get; set; } = 366;

    /// <summary>
    /// 数据保留策略.
    /// </summary>
    public RetentionOptions Retention { get; set; } = new();
}

/// <summary>
/// 数据保留天数（0 = 永久保留）.
/// </summary>
public class RetentionOptions
{
    /// <summary>
    /// 日志保留天数.
    /// </summary>
    public int Logs { get; set; } = 30;

    /// <summary>
    /// 链路保留天数.
    /// </summary>
    public int Traces { get; set; } = 14;

    /// <summary>
    /// 指标保留天数.
    /// </summary>
    public int Metrics { get; set; } = 90;

    /// <summary>
    /// 物联网遥测保留天数.
    /// </summary>
    public int Iot { get; set; } = 365;
}

/// <summary>
/// 认证配置.
/// </summary>
public class AuthOptions
{
    /// <summary>
    /// 首次启动创建的 admin 初始密码（值随发布包 appsettings.json 提供，仅数据库无用户时生效；可用环境变量 SData__Auth__InitialAdminPassword 覆盖）.
    /// </summary>
    public string InitialAdminPassword { get; set; } = string.Empty;

    /// <summary>
    /// 登录 token 有效期（分钟；设置页「运行参数」可改，此处为回退默认）.
    /// </summary>
    public int TokenExpireMinutes { get; set; } = 720;
}

/// <summary>
/// 数据接入配置.
/// </summary>
public class IngestOptions
{
    /// <summary>
    /// 接入端点是否要求鉴权（API Key）.
    /// </summary>
    public bool RequireAuth { get; set; } = true;

    /// <summary>
    /// 首次启动时创建的默认接入 key.
    /// </summary>
    public string InitialApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 内存缓冲容量（条），超过后拒绝写入返回 429.
    /// </summary>
    public int BufferCapacity { get; set; } = 100_000;

    /// <summary>
    /// 内存缓冲估算字节预算；超过后拒绝整批并返回 429.
    /// </summary>
    public long BufferByteBudgetBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// 批量落库条数（DuckDB commit 成本近似固定，批越大摊薄越狠）.
    /// </summary>
    public int BatchSize { get; set; } = 50_000;

    /// <summary>
    /// 落库间隔（毫秒）.
    /// </summary>
    public int FlushIntervalMs { get; set; } = 500;

    /// <summary>
    /// 链路热表 trace_id ART 索引开关. 压测（900 万 span 热表）显示：索引维护让 traces 写入吞吐下降约
    /// 39%，而精确 trace_id 等值查询耗时不变（DuckDB 字典编码 + zone map 扫描即可）；大表规模下可关闭.
    /// </summary>
    public bool TraceIndexEnabled { get; set; } = true;
}

/// <summary>
/// 可选外部 OTLP 出口配置；自身 metrics/trace 固定写入本机，不受此配置控制.
/// </summary>
public class OtlpOptions
{
    /// <summary>
    /// 外部 trace 导出地址（空 = 仅关闭外部出口）.
    /// </summary>
    public string? Trace { get; set; }

    /// <summary>
    /// 外部 metrics 导出地址（空 = 仅关闭外部出口）.
    /// </summary>
    public string? Metrics { get; set; }

    /// <summary>
    /// 导出协议：grpc 或 http/protobuf.
    /// </summary>
    public string Protocol { get; set; } = "grpc";

    /// <summary>外部 trace 请求头（逗号分隔 key=value，包含秘密）.</summary>
    public string TraceHeaders { get; set; } = string.Empty;

    /// <summary>外部 metrics 请求头（逗号分隔 key=value，包含秘密）.</summary>
    public string MetricsHeaders { get; set; } = string.Empty;
}
