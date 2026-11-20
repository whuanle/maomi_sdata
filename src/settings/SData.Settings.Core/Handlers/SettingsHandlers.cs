using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using SData.Infra.Configuration;
using SData.Infra.Exceptions;
using SData.Infra.Services;
using SData.Settings;
using SData.Settings.Services;

namespace SData.Settings.Handlers;

/// <summary>
/// 查询系统设置处理（合并视图：DB 覆盖 &gt; 文件回退；SecretKey 掩码返回）.
/// </summary>
public class QuerySettingsCommandHandler : IRequestHandler<QuerySettingsCommand, SettingsResponse>
{
    private readonly ISystemSettingsProvider _settingsProvider;
    private readonly SystemOptions _systemOptions;
    private readonly IDuckDbRuntimeSettings _duckDb;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuerySettingsCommandHandler"/> class.
    /// </summary>
    /// <param name="settingsProvider">系统设置.</param>
    /// <param name="systemOptions">文件配置（摄取缓冲等只读快照）.</param>
    /// <param name="duckDb">DuckDB 运行参数快照.</param>
    public QuerySettingsCommandHandler(ISystemSettingsProvider settingsProvider, SystemOptions systemOptions, IDuckDbRuntimeSettings duckDb)
    {
        _settingsProvider = settingsProvider;
        _systemOptions = systemOptions;
        _duckDb = duckDb;
    }

    /// <inheritdoc/>
    public Task<SettingsResponse> Handle(QuerySettingsCommand request, CancellationToken cancellationToken)
    {
        var s3 = _settingsProvider.GetS3();
        var response = new SettingsResponse
        {
            S3 = new S3SettingsModel
            {
                Endpoint = s3.Endpoint,
                Bucket = s3.Bucket,
                Prefix = s3.Prefix,
                AccessKey = s3.AccessKey,
                SecretKey = string.IsNullOrWhiteSpace(s3.SecretKey) ? string.Empty : S3SettingsModel.SecretMask,
                HasSecret = !string.IsNullOrWhiteSpace(s3.SecretKey),
            },
            DuckDb = new DuckDbSettingsResponse
            {
                MemoryLimit = _settingsProvider.GetDuckDb().MemoryLimit,
                EffectiveMemoryLimit = _duckDb.EffectiveMemoryLimit,
            },
            Retention = _settingsProvider.GetRetention(),
            Compression = _settingsProvider.GetCompression(),
            Otlp = OtlpHeaders.Redact(_settingsProvider.GetOtlp()),
            Ingest = new IngestSettingsInfo
            {
                RequireAuth = _systemOptions.Ingest.RequireAuth,
                BufferCapacity = _systemOptions.Ingest.BufferCapacity,
                BatchSize = _systemOptions.Ingest.BatchSize,
                FlushIntervalMs = _systemOptions.Ingest.FlushIntervalMs,
            },
        };
        return Task.FromResult(response);
    }
}

/// <summary>保存 DuckDB 内存限制处理.</summary>
public class SaveDuckDbSettingsCommandHandler(DuckDbSettingsService service) : IRequestHandler<SaveDuckDbSettingsCommand, DuckDbSettingsResponse>
{
    /// <inheritdoc/>
    public Task<DuckDbSettingsResponse> Handle(SaveDuckDbSettingsCommand request, CancellationToken cancellationToken)
        => service.SaveAsync(request.MemoryLimit, cancellationToken);
}

/// <summary>
/// 保存 S3 冷存储设置处理：写覆盖 → 立即重建 DuckDB SECRET（立即生效）.
/// </summary>
public class SaveS3SettingsCommandHandler : IRequestHandler<SaveS3SettingsCommand, S3SettingsModel>
{
    private readonly ISystemSettingsProvider _settingsProvider;
    private readonly IS3SecretManager _s3SecretManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaveS3SettingsCommandHandler"/> class.
    /// </summary>
    /// <param name="settingsProvider">系统设置.</param>
    /// <param name="s3SecretManager">S3 SECRET 管理.</param>
    public SaveS3SettingsCommandHandler(ISystemSettingsProvider settingsProvider, IS3SecretManager s3SecretManager)
    {
        _settingsProvider = settingsProvider;
        _s3SecretManager = s3SecretManager;
    }

    /// <inheritdoc/>
    public async Task<S3SettingsModel> Handle(SaveS3SettingsCommand request, CancellationToken cancellationToken)
    {
        var stored = _settingsProvider.GetS3();
        var s3 = new S3Options
        {
            Endpoint = request.Endpoint.Trim(),
            Bucket = request.Bucket.Trim(),
            Prefix = request.Prefix.Trim(),
            ArchiveGranularity = stored.ArchiveGranularity,
            AccessKey = request.AccessKey.Trim(),
            SecretKey = string.IsNullOrWhiteSpace(request.SecretKey) || request.SecretKey == S3SettingsModel.SecretMask
                ? stored.SecretKey
                : request.SecretKey,
        };
        if (!string.IsNullOrWhiteSpace(s3.Endpoint) && (string.IsNullOrWhiteSpace(s3.Bucket) || string.IsNullOrWhiteSpace(s3.AccessKey) || string.IsNullOrWhiteSpace(s3.SecretKey)))
        {
            throw BusinessException.Throw(400, "启用冷存储时 Bucket、AccessKey、SecretKey 均必填");
        }

        await _settingsProvider.SaveAsync(SettingKeys.S3, JsonSerializer.Serialize(s3, SystemSettingsProvider.JsonOptions), cancellationToken);
        await _s3SecretManager.ApplyS3SecretAsync(s3, cancellationToken);
        return new S3SettingsModel
        {
            Endpoint = s3.Endpoint,
            Bucket = s3.Bucket,
            Prefix = s3.Prefix,
            AccessKey = s3.AccessKey,
            SecretKey = string.IsNullOrWhiteSpace(s3.SecretKey) ? string.Empty : S3SettingsModel.SecretMask,
            HasSecret = !string.IsNullOrWhiteSpace(s3.SecretKey),
        };
    }
}

/// <summary>
/// 保存三段保留设置处理.
/// </summary>
public class SaveRetentionSettingsCommandHandler : IRequestHandler<SaveRetentionSettingsCommand, RetentionPlanOptions>
{
    private readonly ISystemSettingsProvider _settingsProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaveRetentionSettingsCommandHandler"/> class.
    /// </summary>
    /// <param name="settingsProvider">系统设置.</param>
    public SaveRetentionSettingsCommandHandler(ISystemSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    /// <inheritdoc/>
    public async Task<RetentionPlanOptions> Handle(SaveRetentionSettingsCommand request, CancellationToken cancellationToken)
    {
        await _settingsProvider.SaveAsync(SettingKeys.Retention, JsonSerializer.Serialize(request.Retention, SystemSettingsProvider.JsonOptions), cancellationToken);
        return request.Retention;
    }
}

/// <summary>
/// 保存数据压缩设置处理.
/// </summary>
public class SaveCompressionSettingsCommandHandler : IRequestHandler<SaveCompressionSettingsCommand, CompressionOptions>
{
    private readonly ISystemSettingsProvider _settingsProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaveCompressionSettingsCommandHandler"/> class.
    /// </summary>
    /// <param name="settingsProvider">系统设置.</param>
    public SaveCompressionSettingsCommandHandler(ISystemSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    /// <inheritdoc/>
    public async Task<CompressionOptions> Handle(SaveCompressionSettingsCommand request, CancellationToken cancellationToken)
    {
        var compression = new CompressionOptions { ArchiveParquetCodec = request.Codec };
        await _settingsProvider.SaveAsync(SettingKeys.Compression, JsonSerializer.Serialize(compression, SystemSettingsProvider.JsonOptions), cancellationToken);
        return compression;
    }
}

/// <summary>
/// 查询 MQ 日志消费实例列表处理.
/// </summary>
public class QueryMqInstancesCommandHandler : IRequestHandler<QueryMqInstancesCommand, List<MqInstanceOptions>>
{
    private readonly ISystemSettingsProvider _settingsProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryMqInstancesCommandHandler"/> class.
    /// </summary>
    /// <param name="settingsProvider">系统设置.</param>
    public QueryMqInstancesCommandHandler(ISystemSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    /// <inheritdoc/>
    public Task<List<MqInstanceOptions>> Handle(QueryMqInstancesCommand request, CancellationToken cancellationToken)
    {
        var instances = JsonSerializer.Deserialize<List<MqInstanceOptions>>(
            JsonSerializer.Serialize(_settingsProvider.GetMqInstances(), SystemSettingsProvider.JsonOptions), SystemSettingsProvider.JsonOptions) ?? [];
        instances.ForEach(MqSecrets.Hide);
        return Task.FromResult(instances);
    }
}

/// <summary>
/// 保存 MQ 日志消费实例列表处理（全量替换写入 system:mq；消费管理器周期检测后自动热更新）.
/// </summary>
public class SaveMqInstancesCommandHandler : IRequestHandler<SaveMqInstancesCommand, SData.Infra.Models.EmptyCommandResponse>
{
    private readonly ISystemSettingsProvider _settingsProvider;
    private readonly IIdProvider _idProvider;
    private readonly SData.Database.DatabaseContext? _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaveMqInstancesCommandHandler"/> class.
    /// </summary>
    /// <param name="settingsProvider">系统设置.</param>
    /// <param name="idProvider">雪花 id（新实例补齐）.</param>
    public SaveMqInstancesCommandHandler(ISystemSettingsProvider settingsProvider, IIdProvider idProvider, SData.Database.DatabaseContext? database = null)
    {
        _settingsProvider = settingsProvider;
        _idProvider = idProvider;
        _database = database;
    }

    /// <inheritdoc/>
    public async Task<SData.Infra.Models.EmptyCommandResponse> Handle(SaveMqInstancesCommand request, CancellationToken cancellationToken)
    {
        var channelIds = request.Instances.Where(x => x.AlertsEnabled).SelectMany(x => x.NotificationChannelIds).Select(long.Parse).Distinct().ToList();
        if (channelIds.Count > 0 && (_database == null || await _database.NotificationChannels.CountAsync(x => channelIds.Contains(x.Id) && x.Enabled, cancellationToken) != channelIds.Count))
            throw new BusinessException("通知渠道不存在或已停用") { StatusCode = 400 };
        var previous = _settingsProvider.GetMqInstances();
        foreach (var instance in request.Instances)
        {
            MqSecrets.Restore(instance, previous.FirstOrDefault(item => item.Id == instance.Id));
            instance.Name = instance.Name.Trim();
            if (instance.Id == 0)
            {
                instance.Id = _idProvider.GetLongId();
            }
        }

        await _settingsProvider.SaveAsync(SettingKeys.Mq, JsonSerializer.Serialize(request.Instances, SystemSettingsProvider.JsonOptions), cancellationToken);
        return new SData.Infra.Models.EmptyCommandResponse();
    }
}

/// <summary>
/// MQ 日志解析预览处理（委托 Ingest.Core 的预览器实现，不落库）.
/// </summary>
public class ParsePreviewCommandHandler : IRequestHandler<ParsePreviewCommand, MqParsePreviewResult>
{
    private readonly IMqLogPreviewer _previewer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ParsePreviewCommandHandler"/> class.
    /// </summary>
    /// <param name="previewer">解析预览器.</param>
    public ParsePreviewCommandHandler(IMqLogPreviewer previewer)
    {
        _previewer = previewer;
    }

    /// <inheritdoc/>
    public Task<MqParsePreviewResult> Handle(ParsePreviewCommand request, CancellationToken cancellationToken)
    {
        var topic = new MqTopicOptions
        {
            Name = "preview",
            Format = request.Format,
            Service = request.Service,
            Severity = request.Severity,
            Parser = request.Parser,
        };
        return Task.FromResult(_previewer.Preview(request.Sample, topic));
    }
}

/// <summary>
/// 保存自身遥测导出设置处理（重启生效）.
/// </summary>
public class SaveOtlpSettingsCommandHandler : IRequestHandler<SaveOtlpSettingsCommand, OtlpOptions>
{
    private readonly ISystemSettingsProvider _settingsProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaveOtlpSettingsCommandHandler"/> class.
    /// </summary>
    /// <param name="settingsProvider">系统设置.</param>
    public SaveOtlpSettingsCommandHandler(ISystemSettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    /// <inheritdoc/>
    public async Task<OtlpOptions> Handle(SaveOtlpSettingsCommand request, CancellationToken cancellationToken)
    {
        var stored = _settingsProvider.GetOtlp();
        var otlp = new OtlpOptions
        {
            Trace = request.Trace.Trim(),
            Metrics = request.Metrics.Trim(),
            Protocol = request.Protocol,
            TraceHeaders = ResolveHeaders(request.TraceHeaders, request.ClearTraceHeaders, stored.TraceHeaders),
            MetricsHeaders = ResolveHeaders(request.MetricsHeaders, request.ClearMetricsHeaders, stored.MetricsHeaders),
        };
        await _settingsProvider.SaveAsync(SettingKeys.Otlp, JsonSerializer.Serialize(otlp, SystemSettingsProvider.JsonOptions), cancellationToken);
        return OtlpHeaders.Redact(otlp);
    }

    private static string ResolveHeaders(string? value, bool clear, string stored)
    {
        if (clear)
        {
            if (!string.IsNullOrEmpty(value))
            {
                throw BusinessException.Throw(400, "清空请求头时不能同时提交新值");
            }

            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(value) || value == OtlpHeaders.SecretMask)
        {
            return stored;
        }

        if (!OtlpHeaders.IsValid(value))
        {
            throw BusinessException.Throw(400, "外部 OTLP 请求头格式无效");
        }

        return OtlpHeaders.Normalize(value);
    }
}

/// <summary>
/// S3 连接测试处理：临时 SECRET + 写测试对象验证连通性.
/// </summary>
public class TestS3ConnectionCommandHandler : IRequestHandler<TestS3ConnectionCommand, TestS3ConnectionResponse>
{
    private readonly ISystemSettingsProvider _settingsProvider;
    private readonly IS3SecretManager _s3SecretManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="TestS3ConnectionCommandHandler"/> class.
    /// </summary>
    /// <param name="settingsProvider">系统设置.</param>
    /// <param name="s3SecretManager">S3 SECRET 管理.</param>
    public TestS3ConnectionCommandHandler(ISystemSettingsProvider settingsProvider, IS3SecretManager s3SecretManager)
    {
        _settingsProvider = settingsProvider;
        _s3SecretManager = s3SecretManager;
    }

    /// <inheritdoc/>
    public async Task<TestS3ConnectionResponse> Handle(TestS3ConnectionCommand request, CancellationToken cancellationToken)
    {
        var stored = _settingsProvider.GetS3();
        var s3 = new S3Options
        {
            Endpoint = request.Endpoint.Trim(),
            Bucket = request.Bucket.Trim(),
            Prefix = request.Prefix.Trim(),
            ArchiveGranularity = stored.ArchiveGranularity,
            AccessKey = request.AccessKey.Trim(),
            SecretKey = string.IsNullOrWhiteSpace(request.SecretKey) || request.SecretKey == S3SettingsModel.SecretMask
                ? stored.SecretKey
                : request.SecretKey,
        };
        if (string.IsNullOrWhiteSpace(s3.SecretKey))
        {
            throw BusinessException.Throw(400, "SecretKey 未配置，无法测试连接");
        }

        try
        {
            await _s3SecretManager.TestConnectionAsync(s3, cancellationToken);
            return new TestS3ConnectionResponse { Success = true, Message = "连接成功，测试对象已写入" };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new TestS3ConnectionResponse { Success = false, Message = $"连接失败：{exception.Message}" };
        }
    }
}

