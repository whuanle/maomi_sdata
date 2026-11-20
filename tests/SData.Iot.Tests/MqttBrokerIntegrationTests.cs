using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using SData.Iot.Mqtt;
using SData.Iot.Services;
using SData.Store;
using SData.Store.Models;
using Xunit;

namespace SData.Iot.Tests;

/// <summary>
/// 进程内 MQTT broker 集成测试（MQTTnet client → MqttBrokerService → IIngestBuffer）.
/// </summary>
public class MqttBrokerIntegrationTests
{
    /// <summary>
    /// 正确凭证按主题约定推单值遥测：QoS1 + retain 发布后写入缓冲.
    /// </summary>
    [Fact]
    public async Task Publish_SingleValue_EnqueuedToBuffer()
    {
        var buffer = new RecordingBuffer();
        using var broker = await StartBrokerAsync(buffer, new StubChecker(("dev1", "secret-1")));
        using var client = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev1", "secret-1");

        var result = await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("iot/dev1/temperature")
            .WithPayload(Encoding.UTF8.GetBytes("""{"v": 21.5, "q": 0}"""))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(true)
            .Build());

        Assert.True(result.IsSuccess, result.ReasonCode.ToString());

        var record = await buffer.WaitForCountAsync(1);
        Assert.Equal("dev1", record.DeviceKey);
        Assert.Equal("temperature", record.Signal);
        Assert.Equal(21.5, record.NumValue);
    }

    /// <summary>
    /// 批量形态：数组消息体逐条入缓冲.
    /// </summary>
    [Fact]
    public async Task Publish_BatchArray_EnqueuesAllRecords()
    {
        var buffer = new RecordingBuffer();
        using var broker = await StartBrokerAsync(buffer, new StubChecker(("dev1", "secret-1")));
        using var client = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev1", "secret-1");

        var result = await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("iot/dev1/batch")
            .WithPayload(Encoding.UTF8.GetBytes("""[{"s": "t1", "v": 1}, {"s": "t2", "v": "OFF"}]"""))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        Assert.True(result.IsSuccess, result.ReasonCode.ToString());
        await buffer.WaitForCountAsync(2);
        Assert.Equal("t1", buffer.Telemetry[0].Signal);
        Assert.Equal("t2", buffer.Telemetry[1].Signal);
    }

    /// <summary>
    /// 两设备发布同名信号：完整主题各自带 deviceKey，记录按 device_key 区分互不覆盖.
    /// </summary>
    [Fact]
    public async Task Publish_SameSignalFromTwoDevices_IsolatedByDeviceKey()
    {
        var buffer = new RecordingBuffer();
        using var broker = await StartBrokerAsync(buffer, new StubChecker(("dev1", "secret-1"), ("dev2", "secret-2")));
        using var client1 = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev1", "secret-1");
        using var client2 = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev2", "secret-2");

        foreach (var (deviceKey, value) in new[] { ("dev1", 11.1), ("dev2", 22.2) })
        {
            var result = await (deviceKey == "dev1" ? client1 : client2).PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic($"iot/{deviceKey}/temperature")
                .WithPayload(Encoding.UTF8.GetBytes($$"""{"v": {{value}}}"""))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build());
            Assert.True(result.IsSuccess, result.ReasonCode.ToString());
        }

        await buffer.WaitForCountAsync(2);
        var byDevice = buffer.Telemetry.ToDictionary(record => record.DeviceKey);
        Assert.Equal(new[] { "dev1", "dev2" }, byDevice.Keys.Order().ToArray());
        Assert.All(byDevice.Values, record => Assert.Equal("temperature", record.Signal));
        Assert.Equal(11.1, byDevice["dev1"].NumValue);
        Assert.Equal(22.2, byDevice["dev2"].NumValue);
    }

    /// <summary>
    /// 同一设备多主题多形态混推：单值对象、多级信号裸数字、批量数组全部入缓冲，
    /// 多级信号名原样保留斜杠形态（落库即 line1/pressure，查询按此形态匹配）.
    /// </summary>
    [Fact]
    public async Task Publish_MultipleTopics_MixedForms_AllEnqueued()
    {
        var buffer = new RecordingBuffer();
        using var broker = await StartBrokerAsync(buffer, new StubChecker(("dev1", "secret-1")));
        using var client = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev1", "secret-1");

        var publishes = new[]
        {
            new MqttApplicationMessageBuilder().WithTopic("iot/dev1/temperature")
                .WithPayload(Encoding.UTF8.GetBytes("""{"v": 21.5}""")).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(),
            new MqttApplicationMessageBuilder().WithTopic("iot/dev1/line1/pressure")
                .WithPayload(Encoding.UTF8.GetBytes("98.7")).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(),
            new MqttApplicationMessageBuilder().WithTopic("iot/dev1/batch")
                .WithPayload(Encoding.UTF8.GetBytes("""[{"s": "t1", "v": 1}, {"s": "state", "value": "RUN"}]""")).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(),
        };
        foreach (var message in publishes)
        {
            var result = await client.PublishAsync(message);
            Assert.True(result.IsSuccess, result.ReasonCode.ToString());
        }

        await buffer.WaitForCountAsync(4);
        Assert.All(buffer.Telemetry, record => Assert.Equal("dev1", record.DeviceKey));
        var expected = new Dictionary<string, double?>
        {
            ["temperature"] = 21.5,
            ["line1/pressure"] = 98.7,
            ["t1"] = 1,
        };
        foreach (var record in buffer.Telemetry)
        {
            if (expected.TryGetValue(record.Signal, out var value))
            {
                Assert.Equal(value, record.NumValue);
            }
            else
            {
                Assert.Equal("state", record.Signal);
                Assert.Equal("RUN", record.StrValue);
            }
        }
    }

    /// <summary>
    /// retain 支持：后续客户端订阅即收到保留消息.
    /// </summary>
    [Fact]
    public async Task Publish_RetainedMessage_ReceivedByLaterSubscriber()
    {
        var buffer = new RecordingBuffer();
        using var broker = await StartBrokerAsync(buffer, new StubChecker(("dev1", "secret-1")));
        using var publisher = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev1", "secret-1");

        await publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("iot/dev1/retained-topic")
            .WithPayload(Encoding.UTF8.GetBytes("""{"v": 1}"""))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(true)
            .Build());

        using var subscriber = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev1", "secret-1");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            received.TrySetResult(MQTTnet.MqttApplicationMessageExtensions.ConvertPayloadToString(args.ApplicationMessage));
            return Task.CompletedTask;
        };
        await subscriber.SubscribeAsync("iot/dev1/retained-topic");

        var payload = await AwaitTaskAsync(received.Task);
        Assert.Contains("\"v\"", payload);
    }

    /// <summary>
    /// 错误密码与未知设备：连接被拒（BadUserNameOrPassword）.
    /// </summary>
    [Fact]
    public async Task Connect_BadCredentials_Rejected()
    {
        var buffer = new RecordingBuffer();
        using var broker = await StartBrokerAsync(buffer, new StubChecker(("dev1", "secret-1")));

        var wrongPassword = await TryConnectAsync(broker.BoundPort!.Value, "dev1", "nope");
        Assert.Equal(MqttClientConnectResultCode.BadUserNameOrPassword, wrongPassword.ResultCode);

        var unknownDevice = await TryConnectAsync(broker.BoundPort!.Value, "ghost", "secret-1");
        Assert.Equal(MqttClientConnectResultCode.BadUserNameOrPassword, unknownDevice.ResultCode);
        Assert.Empty(buffer.Telemetry);
    }

    /// <summary>
    /// 越权主题：dev1 发布 iot/dev2/... 被 PUBACK 拒绝且不入缓冲.
    /// </summary>
    [Fact]
    public async Task Publish_CrossDeviceTopic_RejectedAndNotEnqueued()
    {
        var buffer = new RecordingBuffer();
        using var broker = await StartBrokerAsync(buffer, new StubChecker(("dev1", "secret-1"), ("dev2", "secret-2")));
        using var client = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev1", "secret-1");

        var result = await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("iot/dev2/temperature")
            .WithPayload(Encoding.UTF8.GetBytes("""{"v": 1}"""))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        Assert.False(result.IsSuccess);
        Assert.Equal(MqttClientPublishReasonCode.NotAuthorized, result.ReasonCode);
        Assert.Empty(buffer.Telemetry);
    }

    /// <summary>
    /// 非法消息体：PUBACK PayloadFormatInvalid 且不入缓冲.
    /// </summary>
    [Fact]
    public async Task Publish_InvalidPayload_RejectedWithPayloadFormatInvalid()
    {
        var buffer = new RecordingBuffer();
        using var broker = await StartBrokerAsync(buffer, new StubChecker(("dev1", "secret-1")));
        using var client = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev1", "secret-1");

        var result = await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("iot/dev1/temperature")
            .WithPayload(Encoding.UTF8.GetBytes("not-json"))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        Assert.Equal(MqttClientPublishReasonCode.PayloadFormatInvalid, result.ReasonCode);
        Assert.Empty(buffer.Telemetry);
    }

    /// <summary>
    /// 凭证校验器未注册：fail-closed 拒绝全部连接.
    /// </summary>
    [Fact]
    public async Task Connect_NoCheckerRegistered_RejectedAsNotAuthorized()
    {
        var buffer = new RecordingBuffer();
        using var broker = await StartBrokerAsync(buffer, null);

        var result = await TryConnectAsync(broker.BoundPort!.Value, "dev1", "secret-1");
        Assert.Equal(MqttClientConnectResultCode.NotAuthorized, result.ResultCode);
        Assert.Empty(buffer.Telemetry);
    }

    /// <summary>
    /// 缓冲满：PUBACK QuotaExceeded 背压且不入缓冲.
    /// </summary>
    [Fact]
    public async Task Publish_BufferFull_RejectedWithQuotaExceeded()
    {
        var buffer = new RecordingBuffer { Capacity = 0 };
        using var broker = await StartBrokerAsync(buffer, new StubChecker(("dev1", "secret-1")));
        using var client = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev1", "secret-1");

        var result = await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("iot/dev1/temperature")
            .WithPayload(Encoding.UTF8.GetBytes("""{"v": 1}"""))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        Assert.Equal(MqttClientPublishReasonCode.QuotaExceeded, result.ReasonCode);
        Assert.Empty(buffer.Telemetry);
    }

    /// <summary>
    /// MQTT 始终启动：未配置或遗留 Enabled 值均不能关闭，端口仍可配置.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("invalid")]
    public async Task Start_RegardlessOfLegacyEnabled_BindsPortAndAcceptsConnections(string? legacyEnabled)
    {
        var secret = Guid.NewGuid().ToString("N");
        var port = GetFreePort();
        using var broker = new MqttBrokerService(
            BuildConfiguration(port, legacyEnabled),
            new RecordingBuffer(),
            new IDeviceCredentialChecker[] { new StubChecker(("dev1", secret)) },
            NullLogger<MqttBrokerService>.Instance, new StubRegistry());
        await broker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForBoundAsync(broker);
            Assert.Equal((ushort)port, broker.BoundPort);
            using var client = await ConnectDeviceAsync(port, "dev1", secret);
            Assert.True(client.IsConnected);
        }
        finally
        {
            await broker.StopAsync(CancellationToken.None);
        }

        Assert.Null(broker.BoundPort);
    }

    [Fact]
    public async Task Publish_RegistrationFailure_RetryWithoutEnqueueOrLastSeen()
    {
        var buffer = new RecordingBuffer();
        var registry = new StubRegistry { Fail = true };
        using var broker = await StartBrokerAsync(buffer, new StubChecker(("dev1", "key")), registry);
        using var client = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev1", "key");
        var message = new MqttApplicationMessageBuilder().WithTopic("iot/dev1/temperature")
            .WithPayload("""{"v":1}""").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
        var failed = await client.PublishAsync(message);
        Assert.Equal(MqttClientPublishReasonCode.ImplementationSpecificError, failed.ReasonCode);
        Assert.Empty(buffer.Telemetry);
        Assert.Equal(0, registry.Touches);
        registry.Fail = false;
        Assert.True((await client.PublishAsync(message)).IsSuccess);
        Assert.True((await client.PublishAsync(message)).IsSuccess);
        Assert.Equal(2, registry.Attempts);
        Assert.Equal(2, registry.Touches);
        Assert.Equal(2, buffer.Telemetry.Count);
    }

    private static async Task<MqttBrokerService> StartBrokerAsync(IIngestBuffer buffer, StubChecker? checker, IMqttDeviceRegistry? registry = null)
    {
        var broker = new MqttBrokerService(
            BuildConfiguration(GetFreePort()),
            buffer,
            checker == null ? Array.Empty<IDeviceCredentialChecker>() : new IDeviceCredentialChecker[] { checker },
            NullLogger<MqttBrokerService>.Instance, registry ?? new StubRegistry());
        await broker.StartAsync(CancellationToken.None);
        await WaitForBoundAsync(broker);

        return broker;
    }

    private static IConfiguration BuildConfiguration(int port, string? legacyEnabled = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["SData:Mqtt:Port"] = port.ToString(),
        };
        if (legacyEnabled != null)
        {
            values["SData:Mqtt:Enabled"] = legacyEnabled;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task WaitForBoundAsync(MqttBrokerService broker)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (broker.BoundPort == null)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("broker 未在超时内绑定端口");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>
    /// 一次性连接尝试（不保留客户端，返回连接结果，被拒时得到非 Success 结果码）.
    /// </summary>
    private static async Task<MqttClientConnectResult> TryConnectAsync(int port, string deviceKey, string secret)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        return await client.ConnectAsync(DeviceOptions(port, deviceKey, secret));
    }

    private static MqttClientOptions DeviceOptions(int port, string deviceKey, string secret)
    {
        return new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", port)
            .WithCredentials(deviceKey, secret)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .Build();
    }

    private static async Task<IMqttClient> ConnectDeviceAsync(int port, string deviceKey, string secret)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            await client.ConnectAsync(DeviceOptions(port, deviceKey, secret));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<T> AwaitTaskAsync<T>(Task<T> task)
    {
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (winner != task)
        {
            throw new TimeoutException("等待消息超时");
        }

        return await task;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 测试用凭证校验器：内存设备表 + MqttPasswordHasher（与 T141 存储格式一致）.
    /// </summary>
    private sealed class StubRegistry : IMqttDeviceRegistry
    {
        public long Version { get; private set; }

        public void Invalidate() => Version++;

        public bool Fail { get; set; }

        public int Attempts { get; private set; }

        public int Touches { get; private set; }

        public Task<bool> EnsureAsync(string deviceKey, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Fail) throw new InvalidOperationException("模拟注册失败");
            return Task.FromResult(true);
        }

        public void Touch(string deviceKey, DateTimeOffset receivedAt) => Touches++;
    }

    private sealed class StubChecker : IDeviceCredentialChecker
    {
        private readonly Dictionary<string, string> _secrets;

        public StubChecker(params (string Key, string Secret)[] devices)
        {
            _secrets = devices.ToDictionary(device => device.Key, device => MqttPasswordHasher.Hash(device.Secret));
        }

        public Task<MqttCredentialResult> CheckAsync(string deviceKey, string secret, CancellationToken cancellationToken)
        {
            if (_secrets.TryGetValue(deviceKey, out var storedHash) && MqttPasswordHasher.Verify(secret, storedHash))
            {
                return Task.FromResult(MqttCredentialResult.Ok(deviceKey));
            }

            return Task.FromResult(MqttCredentialResult.Fail(deviceKey, "设备不存在或密钥不匹配"));
        }
    }

    /// <summary>
    /// 测试用记录缓冲：容量可控（模拟缓冲满）.
    /// </summary>
    private sealed class RecordingBuffer : IIngestBuffer
    {
        private readonly object _lock = new();

        public int Capacity { get; init; } = int.MaxValue;

        public List<TelemetryRecord> Telemetry { get; } = new();

        public bool TryEnqueueTelemetry(TelemetryRecord record)
        {
            lock (_lock)
            {
                if (Telemetry.Count >= Capacity)
                {
                    return false;
                }

                Telemetry.Add(record);
                return true;
            }
        }

        public async Task<TelemetryRecord> WaitForCountAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (_lock)
                {
                    if (Telemetry.Count >= count)
                    {
                        return Telemetry[count - 1];
                    }
                }

                await Task.Delay(20);
            }

            throw new TimeoutException($"等待缓冲条数 {count} 超时");
        }

        public bool TryEnqueueLog(LogRecord record) => false;

        public bool TryEnqueueSpan(SpanRecord record) => false;

        public bool TryEnqueueMetric(MetricPointRecord record) => false;

        public bool TryEnqueueRumEvent(RumEventRecord record) => false;

        public bool TryEnqueueRumSession(RumSessionRecord record) => false;

        public bool TryEnqueueRumReplay(RumReplayRecord record) => false;

        public IngestBufferStats GetStats() => new();
    }
}
