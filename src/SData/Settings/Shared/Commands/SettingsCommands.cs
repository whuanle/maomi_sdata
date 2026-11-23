using Mediator;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Infra.Models;
using SData.Infra.Services;

namespace SData.Settings;

/// <summary>
/// 查询系统设置（GET；响应含掩码后的 SecretKey）.
/// </summary>
public class QuerySettingsCommand : IRequest<SettingsResponse>
{
}

/// <summary>
/// 保存 S3 冷存储设置（立即重建 SECRET，立即生效）.
/// </summary>
public class SaveS3SettingsCommand : IRequest<S3SettingsModel>, IModelValidator<SaveS3SettingsCommand>
{
    /// <summary>S3 兼容端点（空 = 关闭冷层）.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>存储桶.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>对象键前缀.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Access Key.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Secret Key（空或掩码 = 沿用已存值）.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SaveS3SettingsCommand> validate)
    {
        validate.RuleFor(x => x.Endpoint).MaximumLength(256);
        validate.RuleFor(x => x.Bucket).MaximumLength(128);
        validate.RuleFor(x => x.Prefix).MaximumLength(128);
        validate.RuleFor(x => x.AccessKey).MaximumLength(128);
        validate.RuleFor(x => x.SecretKey).MaximumLength(256);
    }
}

/// <summary>
/// 保存三段保留设置（5 域热/冷天数，下一轮保留/归档 tick 生效）.
/// </summary>
public class SaveRetentionSettingsCommand : IRequest<RetentionPlanOptions>, IModelValidator<SaveRetentionSettingsCommand>
{
    /// <summary>保留计划.</summary>
    public RetentionPlanOptions Retention { get; set; } = new();

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SaveRetentionSettingsCommand> validate)
    {
        validate.RuleFor(x => x.Retention.Logs.HotDays).InclusiveBetween(0, 36500);
        validate.RuleFor(x => x.Retention.Logs.ColdDays).InclusiveBetween(0, 36500);
        validate.RuleFor(x => x.Retention.Traces.HotDays).InclusiveBetween(0, 36500);
        validate.RuleFor(x => x.Retention.Traces.ColdDays).InclusiveBetween(0, 36500);
        validate.RuleFor(x => x.Retention.Metrics.HotDays).InclusiveBetween(0, 36500);
        validate.RuleFor(x => x.Retention.Metrics.ColdDays).InclusiveBetween(0, 36500);
        validate.RuleFor(x => x.Retention.Iot.HotDays).InclusiveBetween(0, 36500);
        validate.RuleFor(x => x.Retention.Iot.ColdDays).InclusiveBetween(0, 36500);
        validate.RuleFor(x => x.Retention.Rum.HotDays).InclusiveBetween(0, 36500);
        validate.RuleFor(x => x.Retention.Rum.ColdDays).InclusiveBetween(0, 36500);
    }
}

/// <summary>
/// 保存数据压缩设置（冷存档 parquet 压缩编码，下一轮归档 tick 生效）.
/// </summary>
public class SaveCompressionSettingsCommand : IRequest<CompressionOptions>, IModelValidator<SaveCompressionSettingsCommand>
{
    /// <summary>压缩编码：uncompressed / snappy / gzip / zstd.</summary>
    public string Codec { get; set; } = "snappy";

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SaveCompressionSettingsCommand> validate)
    {
        validate.RuleFor(x => x.Codec).NotEmpty().Must(x => CompressionOptions.SupportedCodecs.Contains(x));
    }
}

/// <summary>
/// 保存运行参数（全量替换；DuckDB 线程与并发即时应用，其余字段由各消费端下一次读取生效）.
/// </summary>
public class SaveRuntimeSettingsCommand : IRequest<RuntimeSettings>, IModelValidator<SaveRuntimeSettingsCommand>
{
    /// <summary>运行参数全量.</summary>
    public RuntimeSettings Runtime { get; set; } = new();

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SaveRuntimeSettingsCommand> validate)
    {
        validate.RuleFor(x => x.Runtime.Alerts.MaxEnabledRules).InclusiveBetween(1, 500);
        validate.RuleFor(x => x.Runtime.Alerts.QueryTimeoutSeconds).InclusiveBetween(1, 10);
        validate.RuleFor(x => x.Runtime.Alerts.BudgetSecondsPerMinute).InclusiveBetween(1, 30);
        validate.RuleFor(x => x.Runtime.Agents.RequestsPerMinute).InclusiveBetween(1, 600);
        validate.RuleFor(x => x.Runtime.Agents.QueryTimeoutSeconds).InclusiveBetween(1, 10);
        validate.RuleFor(x => x.Runtime.DuckDb.QueryTimeoutSeconds).InclusiveBetween(1, 300);
        validate.RuleFor(x => x.Runtime.DuckDb.MaxQueryDays).InclusiveBetween(1, 3650);
        validate.RuleFor(x => x.Runtime.DuckDb.ColdMaxQueryDays).InclusiveBetween(1, 3650);
        validate.RuleFor(x => x.Runtime.Auth.TokenExpireMinutes).InclusiveBetween(1, 43200);
    }
}

/// <summary>
/// 查询 MQ 日志消费实例列表（GET；多实例形态，含解析规则参数）.
/// </summary>
public class QueryMqInstancesCommand : IRequest<List<MqInstanceOptions>>, IModelValidator<QueryMqInstancesCommand>
{
    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryMqInstancesCommand> validate)
    {
    }
}

/// <summary>
/// 保存 MQ 日志消费实例列表（全量替换，保存后由消费管理器周期检测自动热更新）.
/// </summary>
public class SaveMqInstancesCommand : IRequest<EmptyCommandResponse>, IModelValidator<SaveMqInstancesCommand>
{
    /// <summary>实例列表（新实例 Id=0，保存时补齐雪花 id）.</summary>
    public List<MqInstanceOptions> Instances { get; set; } = new();

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SaveMqInstancesCommand> validate)
    {
        validate.RuleFor(x => x.Instances).NotNull();
        validate.RuleFor(x => x.Instances)
            .Must(instances => instances.Select(i => i.Name.Trim()).Distinct(StringComparer.Ordinal).Count() == instances.Count)
            .WithMessage("实例名称不能重复")
            .When(x => x.Instances != null!);
        validate.RuleFor(x => x.Instances)
            .Must(items => items.Where(i => i.Id != 0).Select(i => i.Id).Distinct().Count() == items.Count(i => i.Id != 0))
            .WithMessage("实例标识不能重复");
        validate.RuleForEach(x => x.Instances).ChildRules(MqInstanceRules);
    }

    /// <summary>
    /// 单实例校验规则（保存命令与预览命令共用）.
    /// </summary>
    internal static readonly Action<AbstractValidator<MqInstanceOptions>> MqInstanceRules = instance =>
    {
        instance.RuleFor(i => i.Name).NotEmpty().MaximumLength(128);
        instance.RuleFor(i => i.Type).Must(t => t is "kafka" or "rabbitmq" or "pulsar").WithMessage("实例类型仅支持 kafka/rabbitmq/pulsar");
        instance.RuleFor(i => i.BootstrapServers).MaximumLength(512);
        instance.RuleFor(i => i.SecurityProtocol).Must(value => value is "Plaintext" or "SaslPlaintext" or "SaslSsl" or "Ssl");
        instance.RuleFor(i => i.SaslMechanism).Must(value => value is "Plain" or "ScramSha256" or "ScramSha512");
        instance.RuleFor(i => i.GroupId).MaximumLength(128);
        instance.RuleFor(i => i.AutoOffsetReset).Must(x => x is "earliest" or "latest");
        instance.RuleFor(i => i.Uri).MaximumLength(512);
        instance.RuleFor(i => i.ServiceUrl).MaximumLength(512);
        instance.RuleFor(i => i.SubscriptionName).MaximumLength(128);
        instance.RuleFor(i => i.SubscriptionType).Must(x => x is "Shared" or "Failover" or "Exclusive" or "KeyShared");
        instance.RuleFor(i => i)
            .Must(i => !i.Enabled || i.Type switch
            {
                "kafka" => !string.IsNullOrWhiteSpace(i.BootstrapServers),
                "rabbitmq" => !string.IsNullOrWhiteSpace(i.Uri),
                _ => !string.IsNullOrWhiteSpace(i.ServiceUrl),
            })
            .WithMessage(i => $"实例 {i.Name} 已启用但未配置连接参数");
        instance.RuleFor(i => i.RetryInitialSeconds).InclusiveBetween(1, 300);
        instance.RuleFor(i => i.RetryMaxSeconds).InclusiveBetween(1, 3600).GreaterThanOrEqualTo(i => i.RetryInitialSeconds);
        instance.RuleFor(i => i.AlertCooldownMinutes).InclusiveBetween(1, 1440);
        instance.RuleFor(i => i.NotificationChannelIds).NotNull().Must(ids => ids != null && ids.Count <= 10 && ids.Distinct().Count() == ids.Count && ids.All(id => long.TryParse(id, out var value) && value > 0));
        instance.RuleFor(i => i).Must(i => !i.AlertsEnabled || i.NotificationChannelIds is { Count: > 0 }).WithMessage("启用消费告警时必须选择通知渠道");
        instance.RuleFor(i => i.DiscoveryIntervalSeconds).InclusiveBetween(5, 300);
        instance.RuleFor(i => i.AdminUrl)
            .Must(value => string.IsNullOrEmpty(value) || (System.Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)))
            .WithMessage("管理地址必须是 HTTP/HTTPS 地址，凭据请单独配置");
        instance.RuleFor(i => i.Namespace).Matches(@"^[\w.-]+/[\w.-]+$");
        instance.RuleFor(i => i.Topics)
            .Must(rules => rules.Select(r => (r.MatchMode, r.Name)).Distinct().Count() == rules.Count)
            .WithMessage("同一匹配方式的主题规则不能重复");
        instance.RuleFor(i => i)
            .Must(i => i.Type == "kafka" || !i.Topics.Any(t => t.MatchMode == "prefix") || !string.IsNullOrWhiteSpace(i.AdminUrl))
            .WithMessage("RabbitMQ/Pulsar 前缀规则必须配置管理地址");
        instance.RuleForEach(i => i.Topics).ChildRules(topic =>
        {
            topic.RuleFor(t => t.Name).NotEmpty().MaximumLength(256);
            topic.RuleFor(t => t.MatchMode).Must(value => value is "exact" or "prefix").WithMessage("匹配方式仅支持 exact/prefix");
            topic.RuleFor(t => t.Format).Must(MqLogFormat.IsSupported).WithMessage("解析规则仅支持 otlp-proto/otlp-json/json/regex/csv/logfmt/syslog/raw");
            topic.RuleFor(t => t.Service).MaximumLength(128);
            topic.RuleFor(t => t.Severity).MaximumLength(16);
            topic.RuleFor(t => t.Parser).ChildRules(MqParserRules);
            topic.RuleFor(t => t)
                .Must(t => t.Format != MqLogFormat.Regex || TryCompilePattern(t.Parser?.Pattern))
                .WithMessage(t => $"topic {t.Name} 的 regex 正则表达式无法编译（需含命名捕获组定义）");
            topic.RuleFor(t => t)
                .Must(t => t.Format != MqLogFormat.Csv || string.IsNullOrEmpty(t.Parser?.Delimiter) || t.Parser.Delimiter.Length == 1)
                .WithMessage(t => $"topic {t.Name} 的 csv 分隔符必须为单个字符");
        });
    };

    /// <summary>
    /// 解析规则参数校验规则（各格式共用）.
    /// </summary>
    internal static readonly Action<AbstractValidator<MqParserOptions>> MqParserRules = parser =>
    {
        parser.RuleFor(p => p.TimestampField).MaximumLength(128);
        parser.RuleFor(p => p.TimestampFormat).MaximumLength(64);
        parser.RuleFor(p => p.SeverityField).MaximumLength(128);
        parser.RuleFor(p => p.ServiceField).MaximumLength(128);
        parser.RuleFor(p => p.BodyField).MaximumLength(128);
        parser.RuleFor(p => p.TraceIdField).MaximumLength(128);
        parser.RuleFor(p => p.SpanIdField).MaximumLength(128);
        parser.RuleFor(p => p.Pattern).MaximumLength(2048);
        parser.RuleFor(p => p.SeverityMap)
            .Must(m => m == null || (m.Count <= 64 && m.Keys.All(k => k.Length <= 64) && m.Values.All(v => v != null && v.Length <= 16)))
            .WithMessage("级别别名映射的键/值过长或条目过多");
    };

    /// <summary>
    /// 尝试编译正则表达式（校验用；失败返回 false）.
    /// </summary>
    internal static bool TryCompilePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// MQ 日志解析预览（POST；样例文本 + 解析规则 → 映射效果试解析，不落库不进缓冲）.
/// </summary>
public class ParsePreviewCommand : IRequest<MqParsePreviewResult>, IModelValidator<ParsePreviewCommand>
{
    /// <summary>解析规则（MqLogFormat 白名单 8 种）.</summary>
    public string Format { get; set; } = MqLogFormat.Raw;

    /// <summary>默认服务名（解析结果无 service 时使用）.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>默认严重级别（解析结果无级别时使用）.</summary>
    public string Severity { get; set; } = "INFO";

    /// <summary>解析规则参数.</summary>
    public MqParserOptions? Parser { get; set; }

    /// <summary>样例日志文本（可多行）.</summary>
    public string Sample { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<ParsePreviewCommand> validate)
    {
        validate.RuleFor(x => x.Format).Must(MqLogFormat.IsSupported).WithMessage("解析规则仅支持 otlp-proto/otlp-json/json/regex/csv/logfmt/syslog/raw");
        validate.RuleFor(x => x.Service).MaximumLength(128);
        validate.RuleFor(x => x.Severity).MaximumLength(16);
        validate.RuleFor(x => x.Sample).NotEmpty().MaximumLength(100_000);
        validate.RuleFor(x => x.Parser).ChildRules(SaveMqInstancesCommand.MqParserRules);
    }
}

/// <summary>
/// 保存额外外部 OTLP 导出设置（重启生效，本地出口不可修改）.
/// </summary>
public class SaveOtlpSettingsCommand : IRequest<OtlpOptions>, IModelValidator<SaveOtlpSettingsCommand>
{
    /// <summary>外部 trace 导出地址（空 = 仅关闭外部出口）.</summary>
    public string Trace { get; set; } = string.Empty;

    /// <summary>外部 metrics 导出地址（空 = 仅关闭外部出口）.</summary>
    public string Metrics { get; set; } = string.Empty;

    /// <summary>导出协议：grpc / http/protobuf.</summary>
    public string Protocol { get; set; } = "grpc";

    /// <summary>Trace 请求头；空或掩码保留已有值.</summary>
    public string? TraceHeaders { get; set; }

    /// <summary>Metrics 请求头；空或掩码保留已有值.</summary>
    public string? MetricsHeaders { get; set; }

    /// <summary>明确清除 Trace 请求头.</summary>
    public bool ClearTraceHeaders { get; set; }

    /// <summary>明确清除 Metrics 请求头.</summary>
    public bool ClearMetricsHeaders { get; set; }

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SaveOtlpSettingsCommand> validate)
    {
        validate.RuleFor(x => x.Trace).MaximumLength(256).Must(IsOptionalEndpoint)
            .WithMessage("外部链路导出地址必须为 HTTP 或 HTTPS 绝对地址");
        validate.RuleFor(x => x.Metrics).MaximumLength(256).Must(IsOptionalEndpoint)
            .WithMessage("外部指标导出地址必须为 HTTP 或 HTTPS 绝对地址");
        validate.RuleFor(x => x.Protocol).Must(x => x is "grpc" or "http/protobuf");
        validate.RuleFor(x => x.TraceHeaders).Must(IsHeaderInput).WithMessage("Trace 请求头格式无效");
        validate.RuleFor(x => x.MetricsHeaders).Must(IsHeaderInput).WithMessage("Metrics 请求头格式无效");
        validate.RuleFor(x => x).Must(x => !x.ClearTraceHeaders || string.IsNullOrEmpty(x.TraceHeaders))
            .WithMessage("清空 Trace 请求头时不能同时提交新值");
        validate.RuleFor(x => x).Must(x => !x.ClearMetricsHeaders || string.IsNullOrEmpty(x.MetricsHeaders))
            .WithMessage("清空 Metrics 请求头时不能同时提交新值");
    }

    private static bool IsHeaderInput(string? value) => value == OtlpHeaders.SecretMask || OtlpHeaders.IsValid(value);

    private static bool IsOptionalEndpoint(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            || (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var endpoint)
                && endpoint.Scheme is "http" or "https");
    }
}

/// <summary>
/// S3 连接测试（以表单值建临时 SECRET 写测试对象；SecretKey 空/掩码 = 沿用已存值）.
/// </summary>
public class TestS3ConnectionCommand : IRequest<TestS3ConnectionResponse>, IModelValidator<TestS3ConnectionCommand>
{
    /// <summary>S3 兼容端点.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>存储桶.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>对象键前缀.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Access Key.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Secret Key（空或掩码 = 沿用已存值）.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<TestS3ConnectionCommand> validate)
    {
        validate.RuleFor(x => x.Endpoint).NotEmpty().MaximumLength(256);
        validate.RuleFor(x => x.Bucket).NotEmpty().MaximumLength(128);
        validate.RuleFor(x => x.Prefix).MaximumLength(128);
        validate.RuleFor(x => x.AccessKey).NotEmpty().MaximumLength(128);
        validate.RuleFor(x => x.SecretKey).MaximumLength(256);
    }
}
