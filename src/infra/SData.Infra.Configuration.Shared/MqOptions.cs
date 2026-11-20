namespace SData.Infra.Configuration;

/// <summary>
/// MQ 消息格式常量（多实例与解析规则体系；格式白名单供配置校验与消费端分派共用）.
/// </summary>
public static class MqLogFormat
{
    /// <summary>OTLP ExportLogsServiceRequest proto 字节.</summary>
    public const string OtlpProto = "otlp-proto";

    /// <summary>OTLP JSON（resourceLogs/scopeLogs/logRecords）.</summary>
    public const string OtlpJson = "otlp-json";

    /// <summary>JSON 日志对象或对象数组（IngestLogItem 形状，或按 Parser 字段映射提取）.</summary>
    public const string Json = "json";

    /// <summary>正则解析：命名捕获组映射字段（多行逐行匹配，未匹配续行并入上一条正文）.</summary>
    public const string Regex = "regex";

    /// <summary>分隔符解析：按 Delimiter 分列，映射字段填列索引（"0"/"1"/…）.</summary>
    public const string Csv = "csv";

    /// <summary>logfmt 解析：key=value（空格分隔，引号值支持）映射字段.</summary>
    public const string Logfmt = "logfmt";

    /// <summary>syslog 解析：RFC5424 / RFC3164 自动识别.</summary>
    public const string Syslog = "syslog";

    /// <summary>纯文本行（整条消息为日志正文，行首 RFC3339 时间戳嗅探）.</summary>
    public const string Raw = "raw";

    /// <summary>
    /// 校验格式是否支持.
    /// </summary>
    /// <param name="format">格式字符串.</param>
    /// <returns>是否支持.</returns>
    public static bool IsSupported(string? format)
    {
        return format switch
        {
            OtlpProto or OtlpJson or Json or Regex or Csv or Logfmt or Syslog or Raw => true,
            _ => false,
        };
    }
}

/// <summary>
/// MQ 日志消费实例（多实例：每类 MQ 可建 N 个连接，各自带 topic/队列列表与解析规则）.
/// 连接字段按 Type 取用：kafka=BootstrapServers/GroupId/AutoOffsetReset；rabbitmq=Uri；pulsar=ServiceUrl/SubscriptionName/SubscriptionType.
/// </summary>
public class MqInstanceOptions
{
    /// <summary>实例 id（雪花，保存时补齐；消费管理器按它差异化重启）.</summary>
    public long Id { get; set; }

    /// <summary>实例名称（列表内唯一，展示用）.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>实例类型：kafka / rabbitmq / pulsar.</summary>
    public string Type { get; set; } = "kafka";

    /// <summary>是否启用（关闭时不建任何连接）.</summary>
    public bool Enabled { get; set; }

    /// <summary>Kafka broker 地址列表（如 broker1:9092,broker2:9092）.</summary>
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>Kafka 连接安全协议：Plaintext / SaslPlaintext / SaslSsl / Ssl.</summary>
    public string SecurityProtocol { get; set; } = "Plaintext";

    /// <summary>Kafka SASL 机制：Plain / ScramSha256 / ScramSha512.</summary>
    public string SaslMechanism { get; set; } = "ScramSha256";

    /// <summary>Kafka SASL 用户名.</summary>
    public string SaslUsername { get; set; } = string.Empty;

    /// <summary>Kafka SASL 密码.</summary>
    public string SaslPassword { get; set; } = string.Empty;

    /// <summary>Kafka 消费组 id.</summary>
    public string GroupId { get; set; } = "sdata-logs";

    /// <summary>Kafka 无已提交 offset 时的起始位置：earliest / latest.</summary>
    public string AutoOffsetReset { get; set; } = "earliest";

    /// <summary>RabbitMQ AMQP 连接串（amqp://user:pass@host:5672/vhost）.</summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>Pulsar service 地址（pulsar://host:6650）.</summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>Pulsar 订阅名.</summary>
    public string SubscriptionName { get; set; } = "sdata-logs";

    /// <summary>Pulsar 订阅类型：Shared / Failover / Exclusive / KeyShared.</summary>
    public string SubscriptionType { get; set; } = "Shared";

    /// <summary>主题/队列发现的管理 API 地址（前缀规则使用）.</summary>
    public string AdminUrl { get; set; } = string.Empty;

    /// <summary>管理 API 用户名（RabbitMQ）.</summary>
    public string AdminUsername { get; set; } = string.Empty;

    /// <summary>管理 API 密码或 Pulsar Bearer token.</summary>
    public string AdminPassword { get; set; } = string.Empty;

    /// <summary>Pulsar 发现范围：tenant/namespace.</summary>
    public string Namespace { get; set; } = "public/default";

    /// <summary>失败重试的初始间隔与封顶间隔（秒）.</summary>
    public int RetryInitialSeconds { get; set; } = 5;
    public int RetryMaxSeconds { get; set; } = 60;

    /// <summary>消费失败通知；只有显式启用并选择渠道后发送.</summary>
    public bool AlertsEnabled { get; set; }
    public List<string> NotificationChannelIds { get; set; } = [];
    public int AlertCooldownMinutes { get; set; } = 10;

    /// <summary>主题发现间隔（秒）.</summary>
    public int DiscoveryIntervalSeconds { get; set; } = 15;

    /// <summary>消费的 topic/队列列表（每项可配置解析规则）.</summary>
    public List<MqTopicOptions> Topics { get; set; } = new();
}

/// <summary>
/// MQ topic/队列消费配置：名称 + 解析规则（Format 决定解析方式，Service/Severity 为缺省值）.
/// </summary>
public class MqTopicOptions
{
    /// <summary>topic/队列名称.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>匹配方式：exact / prefix（精确优先，其次最长前缀）.</summary>
    public string MatchMode { get; set; } = "exact";

    /// <summary>解析规则（MqLogFormat 白名单 8 种）.</summary>
    public string Format { get; set; } = MqLogFormat.Raw;

    /// <summary>默认服务名（解析结果无 service 时使用）.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>默认严重级别（解析结果无级别时使用）.</summary>
    public string Severity { get; set; } = "INFO";

    /// <summary>解析规则参数（文本类格式生效；null = 结构化格式或 raw 的缺省行为）.</summary>
    public MqParserOptions? Parser { get; set; }
}

/// <summary>
/// 解析规则参数（扁平类，按 Format 取用；未映射字段在 KeepUnmapped=true 时进 attributes）.
/// </summary>
public class MqParserOptions
{
    /// <summary>时间字段（字段名 / 捕获组名 / CSV 列索引；空 = 自动嗅探或消费时间兜底）.</summary>
    public string TimestampField { get; set; } = string.Empty;

    /// <summary>时间格式（DateTimeOffset 精确格式串；空 = 自动嗅探 RFC3339/常用格式/epoch）.</summary>
    public string TimestampFormat { get; set; } = string.Empty;

    /// <summary>级别字段（字段名 / 捕获组名 / 列索引；空 = 用 topic 默认级别）.</summary>
    public string SeverityField { get; set; } = string.Empty;

    /// <summary>级别别名映射（原始值→标准级别，如 W→WARN；归一化别名表之外的值才查它）.</summary>
    public Dictionary<string, string>? SeverityMap { get; set; }

    /// <summary>服务名字段（空 = 用 topic 默认服务名）.</summary>
    public string ServiceField { get; set; } = string.Empty;

    /// <summary>正文字段（空 = 整行原文）.</summary>
    public string BodyField { get; set; } = string.Empty;

    /// <summary>trace id 字段（可选）.</summary>
    public string TraceIdField { get; set; } = string.Empty;

    /// <summary>span id 字段（可选）.</summary>
    public string SpanIdField { get; set; } = string.Empty;

    /// <summary>regex 格式的正则表达式（含命名捕获组）.</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>csv 格式的分隔符（单字符，如 , ; | \t）.</summary>
    public string Delimiter { get; set; } = ",";

    /// <summary>未映射字段是否写入 attributes（默认写入，日志详情可查）.</summary>
    public bool KeepUnmapped { get; set; } = true;
}
