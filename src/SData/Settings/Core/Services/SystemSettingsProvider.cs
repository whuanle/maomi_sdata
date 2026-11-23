using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Configuration;
using SData.Infra.Services;
using SData.Settings;

namespace SData.Settings.Services;

/// <summary>
/// 运行时系统设置提供器：SQLite SettingEntity 覆盖 + 文件 SystemOptions 回退.
/// 懒加载全表到内存字典（首次访问同步加载，失败回退文件默认并告警，不阻塞后台 tick）；
/// 保存即刷新缓存，S3/保留/压缩下一轮后台 tick 生效，MQ 日志消费由管理器周期检测自动热更新，OTLP 重启生效.
/// </summary>
public class SystemSettingsProvider : ISystemSettingsProvider
{
    /// <summary>设置 JSON 序列化选项.</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SystemOptions _systemOptions;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IIdProvider _idProvider;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, string> _overrides = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemSettingsProvider"/> class.
    /// </summary>
    /// <param name="systemOptions">文件系统配置（回退默认）.</param>
    /// <param name="scopeFactory">scope 工厂（SQLite Settings 表）；为空时仅使用文件配置，保存会被拒绝.</param>
    /// <param name="idProvider">雪花 id.</param>
    /// <param name="logger">日志.</param>
    public SystemSettingsProvider(SystemOptions systemOptions, IServiceScopeFactory? scopeFactory, IIdProvider idProvider, ILogger<SystemSettingsProvider> logger)
    {
        _systemOptions = systemOptions;
        _scopeFactory = scopeFactory;
        _idProvider = idProvider;
        _logger = logger;
    }

    /// <summary>
    /// 仅文件配置的提供器（无 SQLite 覆盖；供测试与无库场景，读取即回退 SystemOptions，保存抛出异常）.
    /// </summary>
    /// <param name="systemOptions">文件系统配置.</param>
    /// <returns>提供器.</returns>
    public static SystemSettingsProvider FileOnly(SystemOptions systemOptions)
        => new(systemOptions, null, new DefaultIdProvider(1), NullLogger<SystemSettingsProvider>.Instance);

    /// <inheritdoc/>
    public S3Options GetS3() => Get(SettingKeys.S3, _systemOptions.S3);

    /// <inheritdoc/>
    public RuntimeSettings GetRuntime() => Get(SettingKeys.Runtime, new RuntimeSettings
    {
        Alerts = _systemOptions.Alerts,
        Agents = _systemOptions.Agents,
        DuckDb = new DuckDbTuningOptions
        {
            QueryTimeoutSeconds = _systemOptions.DuckDb.QueryTimeoutSeconds,
            MaxQueryDays = _systemOptions.DuckDb.MaxQueryDays,
            ColdMaxQueryDays = _systemOptions.DuckDb.ColdMaxQueryDays,
        },
        ColdCache = _systemOptions.ColdCache,
        Auth = new AuthTuningOptions { TokenExpireMinutes = _systemOptions.Auth.TokenExpireMinutes },
    });

    /// <inheritdoc/>
    public RetentionPlanOptions GetRetention() => Get(SettingKeys.Retention, _systemOptions.Retention);

    /// <inheritdoc/>
    public CompressionOptions GetCompression()
    {
        var compression = Get(SettingKeys.Compression, _systemOptions.Compression);
        if (!CompressionOptions.SupportedCodecs.Contains(compression.ArchiveParquetCodec))
        {
            return new CompressionOptions();
        }

        return compression;
    }

    /// <inheritdoc/>
    public List<MqInstanceOptions> GetMqInstances()
    {
        EnsureLoaded();
        if (_overrides.TryGetValue(SettingKeys.Mq, out var json))
        {
            try
            {
                return JsonSerializer.Deserialize<List<MqInstanceOptions>>(json, JsonOptions) ?? [];
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "设置 {Key} 反序列化失败，回退旧配置合成", SettingKeys.Mq);
            }
        }

        // 文件级多实例配置（SData:Mq:Instances，安装包默认不配置；日志消费以设置页为准）
        if (_systemOptions.Mq.Count > 0)
        {
            return _systemOptions.Mq;
        }

        // 旧三键合成（单实例形态兼容）：首次保存 system:mq 即完成迁移，旧键此后不再读取
        return SynthesizeLegacyInstances();
    }

    /// <summary>
    /// Kafka 日志消费合并配置（旧单实例形态，仅合成回退用）.
    /// </summary>
    /// <returns>合并后的 Kafka 配置.</returns>
    internal KafkaOptions GetKafka() => Get(SettingKeys.Kafka, _systemOptions.Kafka);

    /// <summary>
    /// RabbitMQ 日志消费合并配置（旧单实例形态，仅合成回退用）.
    /// </summary>
    /// <returns>合并后的 RabbitMQ 配置.</returns>
    internal RabbitMqOptions GetRabbitMq() => Get(SettingKeys.RabbitMq, _systemOptions.RabbitMq);

    /// <summary>
    /// Pulsar 日志消费合并配置（旧单实例形态，仅合成回退用）.
    /// </summary>
    /// <returns>合并后的 Pulsar 配置.</returns>
    internal PulsarOptions GetPulsar() => Get(SettingKeys.Pulsar, _systemOptions.Pulsar);

    /// <summary>
    /// 旧 Kafka/RabbitMq/Pulsar 三节合成单实例列表（配置了连接与 topic 才合成，否则跳过）.
    /// </summary>
    /// <returns>合成实例列表.</returns>
    private List<MqInstanceOptions> SynthesizeLegacyInstances()
    {
        var instances = new List<MqInstanceOptions>();
        var kafka = GetKafka();
        if (kafka.Enabled && !string.IsNullOrWhiteSpace(kafka.BootstrapServers) && kafka.Topics.Count > 0)
        {
            instances.Add(new MqInstanceOptions
            {
                Id = 1,
                Name = "kafka-default",
                Type = "kafka",
                Enabled = true,
                BootstrapServers = kafka.BootstrapServers,
                GroupId = kafka.GroupId,
                AutoOffsetReset = kafka.AutoOffsetReset,
                Topics = kafka.Topics.Select(ToTopicOptions).ToList(),
            });
        }

        var rabbit = GetRabbitMq();
        if (rabbit.Enabled && !string.IsNullOrWhiteSpace(rabbit.Uri) && rabbit.Queues.Count > 0)
        {
            instances.Add(new MqInstanceOptions
            {
                Id = 2,
                Name = "rabbitmq-default",
                Type = "rabbitmq",
                Enabled = true,
                Uri = rabbit.Uri,
                Topics = rabbit.Queues.Select(ToTopicOptions).ToList(),
            });
        }

        var pulsar = GetPulsar();
        if (pulsar.Enabled && !string.IsNullOrWhiteSpace(pulsar.ServiceUrl) && pulsar.Topics.Count > 0)
        {
            instances.Add(new MqInstanceOptions
            {
                Id = 3,
                Name = "pulsar-default",
                Type = "pulsar",
                Enabled = true,
                ServiceUrl = pulsar.ServiceUrl,
                SubscriptionName = pulsar.SubscriptionName,
                SubscriptionType = pulsar.SubscriptionType,
                Topics = pulsar.Topics.Select(ToTopicOptions).ToList(),
            });
        }

        return instances;
    }

    /// <summary>
    /// 旧 MqLogSourceOptions → MqTopicOptions（无解析参数，保持 raw/json 等缺省行为不变）.
    /// </summary>
    private static MqTopicOptions ToTopicOptions(MqLogSourceOptions source)
    {
        return new MqTopicOptions
        {
            Name = source.Name,
            Format = source.Format ?? MqLogFormat.Raw,
            Service = source.Service,
            Severity = source.Severity,
        };
    }

    /// <inheritdoc/>
    public OtlpOptions GetOtlp() => Get(SettingKeys.Otlp, _systemOptions.OTLP);

    /// <inheritdoc/>
    public MqttAuthOptions GetMqttAuth()
    {
        EnsureLoaded();
        if (!_loaded) return new() { Mode = "invalid" };
        if (!_overrides.TryGetValue(SettingKeys.MqttAuth, out var json)) return new();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("mode", out var mode)
                || mode.ValueKind != JsonValueKind.String) return new() { Mode = "invalid" };
            return JsonSerializer.Deserialize<MqttAuthOptions>(json, JsonOptions) ?? new() { Mode = "invalid" };
        }
        catch (JsonException)
        {
            _logger.LogWarning("MQTT 认证设置无效，拒绝连接");
            return new() { Mode = "invalid" };
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(string key, string json, CancellationToken cancellationToken)
    {
        var scopes = _scopeFactory ?? throw new InvalidOperationException("仅文件配置模式不支持保存设置");
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            var existing = await db.Settings.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
            if (existing == null)
            {
                db.Settings.Add(new SettingEntity { Id = _idProvider.GetLongId(), Key = key, Value = json });
            }
            else
            {
                existing.Value = json;
            }

            await db.SaveChangesAsync(cancellationToken);
            _overrides[key] = json;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// 读取某设置键的覆盖值（宿主构建期使用，不触发 EF；库未就绪一律返回 null）.
    /// </summary>
    /// <param name="sqlitePath">SQLite 元数据库文件路径.</param>
    /// <param name="key">设置键.</param>
    /// <returns>覆盖 JSON 或 null.</returns>
    public static string? TryReadOverride(string sqlitePath, string key)
    {
        try
        {
            if (!File.Exists(sqlitePath))
            {
                return null;
            }

            using var connection = new SqliteConnection($"Data Source={sqlitePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM setting WHERE Key = $key";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$key";
            parameter.Value = key;
            command.Parameters.Add(parameter);
            return command.ExecuteScalar() as string;
        }
        catch
        {
            // 宿主构建期尽力读取：库未就绪/表缺失/锁定一律回退文件配置
            return null;
        }
    }

    /// <summary>
    /// 读取 OTLP 导出覆盖设置（宿主构建期使用；首次启动无库/无覆盖/反序列化失败均返回 null）.
    /// </summary>
    /// <param name="sqlitePath">SQLite 元数据库文件路径.</param>
    /// <returns>OTLP 覆盖或 null.</returns>
    public static OtlpOptions? TryLoadOtlpOverride(string sqlitePath)
    {
        var json = TryReadOverride(sqlitePath, SettingKeys.Otlp);
        if (json == null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OtlpOptions>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 合并读取：DB 覆盖 &gt; 文件回退；反序列化失败回退文件默认（脏数据不阻塞业务）.
    /// </summary>
    /// <typeparam name="T">选项类型.</typeparam>
    /// <param name="key">设置键.</param>
    /// <param name="fallback">文件默认.</param>
    /// <returns>合并结果.</returns>
    private T Get<T>(string key, T fallback) where T : class
    {
        EnsureLoaded();
        if (!_overrides.TryGetValue(key, out var json))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "设置 {Key} 反序列化失败，回退文件配置", key);
            return fallback;
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loadLock.Wait();
        try
        {
            if (_loaded)
            {
                return;
            }

            // 仅文件配置模式（无库）：没有覆盖可加载，直接视为已加载
            if (_scopeFactory is null)
            {
                _loaded = true;
                return;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
                foreach (var setting in db.Settings.ToList())
                {
                    _overrides[setting.Key] = setting.Value;
                }
            }
            catch (Exception exception)
            {
                // 首次启动建库未完成/库暂不可用时回退文件默认，不阻塞后台 tick
                _logger.LogWarning(exception, "系统设置加载失败，暂以文件配置运行");
                return;
            }

            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
