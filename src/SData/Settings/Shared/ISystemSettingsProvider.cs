using SData.Infra.Configuration;

namespace SData.Settings;

/// <summary>
/// 系统设置键（SQLite SettingEntity 的 Key 常量；值为对应选项类型的 JSON 序列化）.
/// </summary>
public static class SettingKeys
{
    /// <summary>S3 冷存储（S3Options）.</summary>
    public const string S3 = "system:s3";

    /// <summary>三段保留（RetentionPlanOptions）.</summary>
    public const string Retention = "system:retention";

    /// <summary>运行参数（RuntimeSettings；设置页「运行参数」，保存后热生效）.</summary>
    public const string Runtime = "system:runtime";

    /// <summary>数据压缩（CompressionOptions）.</summary>
    public const string Compression = "system:compression";

    /// <summary>Kafka 日志消费（KafkaOptions）.</summary>
    public const string Kafka = "system:kafka";

    /// <summary>RabbitMQ 日志消费（RabbitMqOptions）.</summary>
    public const string RabbitMq = "system:rabbitmq";

    /// <summary>Pulsar 日志消费（PulsarOptions；旧单实例形态，仅作多实例未保存前的合成回退）.</summary>
    public const string Pulsar = "system:pulsar";

    /// <summary>MQ 日志消费实例列表（List&lt;MqInstanceOptions&gt;，多实例形态主存储）.</summary>
    public const string Mq = "system:mq";

    /// <summary>自身遥测导出（OtlpOptions）.</summary>
    public const string Otlp = "system:otlp";

    public const string MqttAuth = "system:mqtt-auth";
}

/// <summary>
/// 运行时系统设置通道：设置页保存的覆盖值存 SQLite SettingEntity，读取时合并（DB 覆盖 &gt; 文件 SystemOptions 回退）.
/// S3/保留/压缩为立即生效（后台服务每轮 tick 重读）；运行参数由各消费端每次读取，保存即生效；MQ 日志消费由管理器周期检测自动热更新；OTLP 为重启生效.
/// </summary>
public interface ISystemSettingsProvider
{
    /// <summary>
    /// S3 冷存储合并配置.
    /// </summary>
    /// <returns>合并后的 S3 配置.</returns>
    S3Options GetS3();

    /// <summary>
    /// 运行参数合并配置（DB 覆盖 &gt; 文件回退；每次调用读取，保存即生效）.
    /// </summary>
    /// <returns>合并后的运行参数.</returns>
    RuntimeSettings GetRuntime();

    /// <summary>
    /// 三段保留合并配置.
    /// </summary>
    /// <returns>合并后的保留计划.</returns>
    RetentionPlanOptions GetRetention();

    /// <summary>
    /// 数据压缩合并配置（编码不在白名单时回退 snappy）.
    /// </summary>
    /// <returns>合并后的压缩配置.</returns>
    CompressionOptions GetCompression();

    /// <summary>
    /// MQ 日志消费实例列表合并配置（读取顺序：设置键 system:mq &gt; 文件 Mq 节 &gt; 旧 Kafka/RabbitMq/Pulsar 三键内存合成；首次保存即完成迁移）.
    /// </summary>
    /// <returns>实例列表（可能为空）.</returns>
    List<MqInstanceOptions> GetMqInstances();

    /// <summary>
    /// 自身遥测导出合并配置.
    /// </summary>
    /// <returns>合并后的 OTLP 配置.</returns>
    OtlpOptions GetOtlp();

    /// <summary>MQTT 认证配置；未实现的提供器拒绝认证，避免静默免 key.</summary>
    MqttAuthOptions GetMqttAuth() => new() { Mode = "invalid" };

    /// <summary>
    /// 保存设置覆盖值（upsert SettingEntity + 刷新缓存）.
    /// </summary>
    /// <param name="key">设置键（SettingKeys）.</param>
    /// <param name="json">选项 JSON.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    Task SaveAsync(string key, string json, CancellationToken cancellationToken);
}

/// <summary>
/// S3 SECRET 管理（契约在 Settings.Shared，实现经 DuckDB httpfs，由 Store.Core 注册）.
/// </summary>
public interface IS3SecretManager
{
    /// <summary>
    /// 按给定 S3 配置重建 DuckDB SECRET（Endpoint/Bucket 为空时跳过 = 关闭冷层）.
    /// </summary>
    /// <param name="s3">S3 配置.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    Task ApplyS3SecretAsync(S3Options s3, CancellationToken cancellationToken);

    /// <summary>
    /// 连接测试：以临时 SECRET 写入测试对象验证连通性（对象保留在桶内，体积数百字节）.
    /// </summary>
    /// <param name="s3">S3 配置.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    Task TestConnectionAsync(S3Options s3, CancellationToken cancellationToken);
}
