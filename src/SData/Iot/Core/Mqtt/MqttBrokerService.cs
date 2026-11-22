using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet.Protocol;
using MQTTnet.Server;
using SData.Iot.Services;
using SData.Store;

namespace SData.Iot.Mqtt;

/// <summary>
/// 内嵌 MQTT broker 后台服务（MQTTnet 5.2，QoS 0/1，retain 支持，本机单实例无集群）.
/// 接入约定：username = DeviceKey，password 按接入认证策略校验；未知设备首次有效发布自动注册；
/// ACL：仅允许发布 iot/{自身 deviceKey}/... 主题；
/// 遥测经 MqttTopicProtocol 解析后写入 IIngestBuffer.TryEnqueueTelemetry（缓冲满整条拒绝，设备端可重试）.
/// 随宿主始终启动，不提供启停开关；SData:Mqtt:Port 配置监听端口（默认 1883）.
/// </summary>
public class MqttBrokerService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IIngestBuffer _buffer;
    private readonly IDeviceCredentialChecker[] _checkers;
    private readonly ILogger<MqttBrokerService> _logger;
    private readonly IMqttDeviceRegistry _registry;
    private readonly ConcurrentDictionary<string, DeviceSession> _clientDevices = new();

    private sealed class DeviceSession(string deviceKey)
    {
        public string DeviceKey { get; } = deviceKey;

        public long CheckedVersion { get; set; } = -1;

        public bool Allowed { get; set; }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MqttBrokerService"/> class.
    /// </summary>
    /// <param name="configuration">配置.</param>
    /// <param name="buffer">接入缓冲.</param>
    /// <param name="checkers">设备凭证校验器（T141 提供运行时实现；未注册时拒绝全部连接）.</param>
    /// <param name="logger">日志.</param>
    /// <param name="registry">首次有效发布注册与活跃时间累积.</param>
    public MqttBrokerService(IConfiguration configuration, IIngestBuffer buffer, IEnumerable<IDeviceCredentialChecker> checkers, ILogger<MqttBrokerService> logger, IMqttDeviceRegistry registry)
    {
        _configuration = configuration;
        _buffer = buffer;
        _checkers = checkers.ToArray();
        _logger = logger;
        _registry = registry;
    }

    /// <summary>
    /// 已绑定的监听端口（尚未绑定或已停止时为 null）.
    /// </summary>
    public ushort? BoundPort { get; private set; }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = int.TryParse(_configuration["SData:Mqtt:Port"], out var parsedPort) && parsedPort is > 0 and <= 65535 ? parsedPort : 1883;
        if (_checkers.Length == 0)
        {
            _logger.LogWarning("MQTT broker 未注册设备凭证校验器（IDeviceCredentialChecker），所有连接将被拒绝；待设备凭证服务就绪后重启生效");
        }

        var factory = new MqttServerFactory();
        var server = factory.CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(port)
            .Build());
        server.ValidatingConnectionAsync += ValidateConnectionAsync;
        server.InterceptingPublishAsync += InterceptApplicationMessageAsync;
        server.ClientDisconnectedAsync += OnClientDisconnectedAsync;
        try
        {
            await server.StartAsync();
            BoundPort = (ushort)port;
            _logger.LogInformation("MQTT broker 已启动（port={Port}，QoS 0/1，retain 支持）", BoundPort);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        finally
        {
            await server.StopAsync(factory.CreateMqttServerStopOptionsBuilder().Build());
            server.Dispose();
            BoundPort = null;
            _logger.LogInformation("MQTT broker 已停止");
        }
    }

    private async Task ValidateConnectionAsync(ValidatingConnectionEventArgs args)
    {
        var deviceKey = args.UserName ?? string.Empty;
        var secret = args.Password ?? string.Empty;
        if (!DeviceKeyRules.IsValid(deviceKey))
        {
            args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            return;
        }

        if (_checkers.Length == 0)
        {
            _logger.LogWarning("拒绝 MQTT 连接 client={ClientId}：设备凭证校验器未就绪", args.ClientId);
            args.ReasonCode = MqttConnectReasonCode.NotAuthorized;
            return;
        }

        var result = await _checkers[0].CheckAsync(deviceKey, secret, args.CancellationToken);
        if (!result.Success)
        {
            _logger.LogWarning("拒绝 MQTT 连接 client={ClientId} device={DeviceKey}：{Reason}", args.ClientId, deviceKey, result.Reason);
            args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            return;
        }

        _clientDevices[args.ClientId] = new DeviceSession(result.DeviceKey!);
        args.ReasonCode = MqttConnectReasonCode.Success;
    }

    private async Task InterceptApplicationMessageAsync(InterceptingPublishEventArgs args)
    {
        if (!_clientDevices.TryGetValue(args.ClientId, out var session))
        {
            args.ProcessPublish = false;
            args.Response.ReasonCode = MqttPubAckReasonCode.NotAuthorized;
            return;
        }

        var deviceKey = session.DeviceKey;
        var receivedAt = DateTimeOffset.UtcNow;
        var topic = args.ApplicationMessage.Topic ?? string.Empty;
        if (!topic.StartsWith($"iot/{deviceKey}/", StringComparison.Ordinal))
        {
            _logger.LogWarning("拒绝越权发布 client={ClientId} device={DeviceKey} topic={Topic}", args.ClientId, deviceKey, topic);
            args.ProcessPublish = false;
            args.Response.ReasonCode = MqttPubAckReasonCode.NotAuthorized;
            return;
        }

        var payload = PayloadToArray(args.ApplicationMessage.Payload);
        if (!MqttTopicProtocol.TryParsePayload(topic, payload, receivedAt, out var records, out var error))
        {
            _logger.LogWarning("拒绝非法消息体 client={ClientId} device={DeviceKey} topic={Topic}：{Error}", args.ClientId, deviceKey, topic, error);
            args.ProcessPublish = false;
            args.Response.ReasonCode = MqttPubAckReasonCode.PayloadFormatInvalid;
            return;
        }

        try
        {
            while (session.CheckedVersion != _registry.Version)
            {
                var version = _registry.Version;
                var allowed = await _registry.EnsureAsync(deviceKey, args.CancellationToken);
                if (version != _registry.Version)
                {
                    continue;
                }

                session.Allowed = allowed;
                session.CheckedVersion = version;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "MQTT 注册或状态校验失败 device={DeviceKey}", deviceKey);
            args.ProcessPublish = false;
            args.Response.ReasonCode = MqttPubAckReasonCode.ImplementationSpecificError;
            return;
        }

        if (!session.Allowed)
        {
            args.ProcessPublish = false;
            args.Response.ReasonCode = MqttPubAckReasonCode.NotAuthorized;
            return;
        }

        foreach (var record in records)
        {
            if (!_buffer.TryEnqueueTelemetry(record))
            {
                _logger.LogWarning("接入缓冲已满，拒绝 MQTT 消息 device={DeviceKey} topic={Topic}（count={Count}）", deviceKey, topic, records.Count);
                args.ProcessPublish = false;
                args.Response.ReasonCode = MqttPubAckReasonCode.QuotaExceeded;
                return;
            }
        }

        _registry.Touch(deviceKey, receivedAt);
        args.ProcessPublish = true;
    }

    private Task OnClientDisconnectedAsync(ClientDisconnectedEventArgs args)
    {
        _clientDevices.TryRemove(args.ClientId, out _);
        return Task.CompletedTask;
    }

    private static byte[] PayloadToArray(System.Buffers.ReadOnlySequence<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return [];
        }

        using var stream = new MemoryStream();
        foreach (var segment in payload)
        {
            stream.Write(segment.Span);
        }

        return stream.ToArray();
    }
}
