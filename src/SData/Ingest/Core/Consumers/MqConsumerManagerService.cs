using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SData.Infra.Configuration;
using SData.Settings;
using SData.Store;

namespace SData.Ingest.Consumers;

/// <summary>
/// MQ 消费管理器（多实例热更新）：启动及每轮 tick 读取实例列表，按实例指纹（配置 JSON）差异化启停消费者.
/// 新增/修改/删除/停用实例 → 对应消费者自动停止重建，无需重启 SData；Enabled=false 或缺连接串/topic 的实例不建任何连接.
/// </summary>
public class MqConsumerManagerService : BackgroundService, IMqConsumerControl
{
    /// <summary>配置检测周期（秒）.</summary>
    private const int PollIntervalSeconds = 10;

    /// <summary>停机等待消费者退出的最长时限.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISystemSettingsProvider _settingsProvider;
    private readonly IIngestBuffer _buffer;
    private readonly IMqLogDecoder _decoder;
    private readonly ILogger<MqConsumerManagerService> _logger;
    private readonly Dictionary<long, ConsumerHandle> _running = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _retryRequests = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private CancellationToken _stoppingToken;

    public bool RequestRetry(long instanceId)
    {
        if (!_settingsProvider.GetMqInstances().Any(x => x.Id == instanceId && IsRunnable(x))) return false;
        _retryRequests.TryAdd(instanceId, 0);
        try { _wake.Release(); } catch (SemaphoreFullException) { }
        return true;
    }

    /// <summary>
    /// 各类型实例的消费入口委托表（构造时注册，进程内后台消费任务，无任何外部命令执行）.
    /// </summary>
    private readonly Dictionary<string, Func<MqInstanceOptions, CancellationToken, Task>> _consumerEntries;

    /// <summary>
    /// Initializes a new instance of the <see cref="MqConsumerManagerService"/> class.
    /// </summary>
    /// <param name="settingsProvider">运行时系统设置（实例列表）.</param>
    /// <param name="buffer">接入缓冲.</param>
    /// <param name="decoder">消息解码.</param>
    /// <param name="logger">日志.</param>
    public MqConsumerManagerService(ISystemSettingsProvider settingsProvider, IIngestBuffer buffer, IMqLogDecoder decoder, ILogger<MqConsumerManagerService> logger)
    {
        _settingsProvider = settingsProvider;
        _buffer = buffer;
        _decoder = decoder;
        _logger = logger;
        _consumerEntries = new Dictionary<string, Func<MqInstanceOptions, CancellationToken, Task>>(StringComparer.Ordinal)
        {
            ["kafka"] = (instance, token) => KafkaInstanceRunner.ConsumeAsync(instance, _buffer, _decoder, _logger, token),
            ["rabbitmq"] = (instance, token) => RabbitMqInstanceRunner.ConsumeAsync(instance, _buffer, _decoder, _logger, token),
            ["pulsar"] = (instance, token) => PulsarInstanceRunner.ConsumeAsync(instance, _buffer, _decoder, _logger, token),
        };
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _logger.LogInformation("MQ 消费管理器启动（多实例，每 {Seconds}s 检测配置变化自动热更新）", PollIntervalSeconds);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ReconcileAsync();
                await _wake.WaitAsync(TimeSpan.FromSeconds(PollIntervalSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停机取消，走统一清理
        }

        await ShutdownAllAsync();
    }

    /// <summary>
    /// 一轮协调：读取期望实例集 → 停掉不在期望集或指纹变化的运行实例 → 启动新增/变化的实例.
    /// </summary>
    public async Task ReconcileAsync()
    {
        await _reconcileGate.WaitAsync();
        try { await ReconcileCoreAsync(); }
        finally { _reconcileGate.Release(); }
    }

    private async Task ReconcileCoreAsync()
    {
        Dictionary<long, (MqInstanceOptions Instance, string Fingerprint)> desired;
        List<MqInstanceOptions> configured;
        try
        {
            configured = _settingsProvider.GetMqInstances();
            desired = ComputeDesired(configured);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "MQ 实例配置读取失败，本轮维持现状");
            return;
        }

        foreach (var id in _retryRequests.Keys.ToList())
        {
            if (!desired.ContainsKey(id)) { _retryRequests.TryRemove(id, out _); continue; }
            await ShutdownConsumerAsync(id, "管理员立即重试");
            if (!_running.ContainsKey(id)) _retryRequests.TryRemove(id, out _);
        }

        foreach (var id in _running.Keys.ToList())
        {
            if (desired.TryGetValue(id, out var target) && target.Fingerprint == _running[id].Fingerprint && !_running[id].Task.IsCompleted && !_running[id].Cts.IsCancellationRequested)
            {
                continue;
            }

            await ShutdownConsumerAsync(id, desired.ContainsKey(id) ? "配置变更或任务结束" : "实例已删除或停用");
        }

        var configuredIds = configured.Select(x => x.Id).ToHashSet();
        foreach (var item in MqRuntime.Snapshot())
        {
            if (!configuredIds.Contains(item.InstanceId) && !_running.ContainsKey(item.InstanceId))
                MqRuntime.Remove(item.InstanceId);
        }

        foreach (var instance in configured.Where(x => !desired.ContainsKey(x.Id) && !_running.ContainsKey(x.Id)))
            MqRuntime.SetState(instance.Id, instance.Enabled ? "error" : "disabled", instance.Enabled ? "消费配置不完整或解析规则无效" : null);

        foreach (var (id, (instance, fingerprint)) in desired)
        {
            if (_running.ContainsKey(id))
            {
                continue;
            }

            LaunchConsumer(id, instance, fingerprint);
        }
    }

    /// <summary>
    /// 计算期望实例集：过滤不可运行实例，生成 id → (实例, 配置指纹)（指纹变化即触发重启）.
    /// </summary>
    /// <param name="instances">实例列表.</param>
    /// <returns>期望运行集.</returns>
    public static Dictionary<long, (MqInstanceOptions Instance, string Fingerprint)> ComputeDesired(List<MqInstanceOptions> instances)
    {
        var desired = new Dictionary<long, (MqInstanceOptions, string)>();
        foreach (var instance in instances)
        {
            if (!IsRunnable(instance))
            {
                continue;
            }

            desired[instance.Id] = (instance, JsonSerializer.Serialize(instance, JsonOptions));
        }

        return desired;
    }

    /// <summary>
    /// 实例是否可运行：已启用、类型合法、连接串与有效 topic 齐备.
    /// </summary>
    public static bool IsRunnable(MqInstanceOptions instance)
    {
        if (!instance.Enabled)
        {
            return false;
        }

        var topics = ValidTopics(instance);
        return instance.Type switch
        {
            "kafka" => topics.Count > 0 && !string.IsNullOrWhiteSpace(instance.BootstrapServers),
            "rabbitmq" => topics.Count > 0 && !string.IsNullOrWhiteSpace(instance.Uri),
            "pulsar" => topics.Count > 0 && !string.IsNullOrWhiteSpace(instance.ServiceUrl),
            _ => false,
        };
    }

    /// <summary>
    /// 有效 topic 列表（名称非空且解析规则在白名单内；无效项由 Runner 记 warning 忽略）.
    /// </summary>
    public static List<MqTopicOptions> ValidTopics(MqInstanceOptions instance)
    {
        return instance.Topics
            .Where(t => !string.IsNullOrWhiteSpace(t.Name) && MqLogFormat.IsSupported(t.Format))
            .ToList();
    }

    private void LaunchConsumer(long id, MqInstanceOptions instance, string fingerprint)
    {
        if (!_consumerEntries.TryGetValue(instance.Type, out var entry))
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
        Task task;
        try
        {
            MqRuntime.Start(id, instance.Topics.Select(t => (t.Name, t.MatchMode)));
            task = Task.Run(() => entry(instance, cts.Token), CancellationToken.None);
        }
        catch (Exception exception)
        {
            // 线程池拒绝等极端情形：不启动，下一轮重试
            _logger.LogWarning(exception, "MQ 消费者启动失败（实例={Name}），下轮重试", instance.Name);
            cts.Dispose();
            return;
        }

        _running[id] = new ConsumerHandle(fingerprint, task, cts);
        _logger.LogInformation("MQ 消费者已启动（实例={Name}，类型={Type}，topics={Topics}）", instance.Name, instance.Type, string.Join(",", ValidTopics(instance).Select(t => t.Name)));
    }

    private async Task ShutdownConsumerAsync(long id, string reason)
    {
        if (!_running.TryGetValue(id, out var handle))
        {
            return;
        }

        handle.Cts.Cancel();
        if (await Task.WhenAny(handle.Task, Task.Delay(ShutdownGrace)) != handle.Task)
        {
            _logger.LogWarning("MQ 消费者尚未退出，延后重建（{Reason}）", reason);
            return;
        }

        try
        {
            await handle.Task;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "MQ 消费者退出");
        }

        _running.Remove(id);
        MqRuntime.SetState(id, "stopped");
        handle.Cts.Dispose();
        _logger.LogInformation("MQ 消费者已停止（{Reason}）", reason);
    }

    private async Task ShutdownAllAsync()
    {
        var handles = _running.Values.ToList();
        _running.Clear();
        foreach (var handle in handles)
        {
            handle.Cts.Cancel();
        }

        if (handles.Count == 0)
        {
            return;
        }

        // 有界等待消费者退出；超时不阻塞宿主停机（消费循环会随取消令牌自行结束）
        var allStopped = Task.WhenAll(handles.Select(h => h.Task));
        await Task.WhenAny(allStopped, Task.Delay(ShutdownGrace));
    }

    /// <summary>
    /// 单实例运行态：配置指纹 + 消费任务 + 取消令牌.
    /// </summary>
    private sealed class ConsumerHandle(string fingerprint, Task task, CancellationTokenSource cts)
    {
        public string Fingerprint { get; } = fingerprint;

        public Task Task { get; } = task;

        public CancellationTokenSource Cts { get; } = cts;
    }
}
