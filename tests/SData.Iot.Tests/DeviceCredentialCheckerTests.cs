using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using SData.Database;
using SData.Database.Entities;
using SData.Iot;
using SData.Iot.Handlers;
using SData.Iot.Mqtt;
using SData.Iot.Services;
using SData.Infra.Services;
using SData.Settings;
using SData.Infra.Configuration;
using SData.Store;
using SData.Store.Models;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace SData.Iot.Tests;

/// <summary>
/// T140↔T141 衔接闭环验证：DeviceCredentialService 作为 IDeviceCredentialChecker 注册进容器，
/// MQTT broker 用真实 SQLite 设备台账校验免密码 CONNECT.
/// </summary>
public class DeviceCredentialCheckerTests
{
    /// <summary>
    /// IotCoreModule 注册 IDeviceCredentialChecker 且与 DeviceCredentialService 同一单例.
    /// </summary>
    [Fact]
    public void Module_RegistersChecker_AsDeviceCredentialService_SameInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<DeviceLastSeenBuffer>();
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        services.AddSingleton(options);
        services.AddScoped<DatabaseContext>(sp => new DatabaseContext(sp.GetRequiredService<DbContextOptions<DatabaseContext>>()));

        var context = new Maomi.DefaultServiceContext(services, new ConfigurationBuilder().Build());
        new IotCoreModule().ConfigureServices(context);

        using var provider = services.BuildServiceProvider();
        var checker = provider.GetRequiredService<IDeviceCredentialChecker>();
        Assert.IsType<DeviceCredentialService>(checker);
        Assert.Same(provider.GetRequiredService<DeviceCredentialService>(), checker);
    }

    /// <summary>
    /// 已注册启用设备无密码连接放行，按主题约定发布遥测入缓冲.
    /// </summary>
    [Fact]
    public async Task Broker_RegisteredDevice_NoPassword_PublishEnqueued()
    {
        using var connection = CreateDeviceDatabase(("dev-real", true), ("dev-off", false));
        var options = BuildOptions(connection);
        using var provider = CreateProvider(options);
        var checker = provider.GetRequiredService<DeviceCredentialService>();

        var buffer = new RecordingBuffer();
        using var broker = await StartBrokerAsync(buffer, checker, provider.GetRequiredService<IMqttDeviceRegistry>());
        using var client = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev-real", string.Empty);

        var result = await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("iot/dev-real/temperature")
            .WithPayload(Encoding.UTF8.GetBytes("""{"v": 42, "q": 0}"""))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

        Assert.True(result.IsSuccess, result.ReasonCode.ToString());
        var record = await buffer.WaitForCountAsync(1);
        Assert.Equal("dev-real", record.DeviceKey);
        Assert.Equal("temperature", record.Signal);
        Assert.Equal(42, record.NumValue);
    }

    /// <summary>
    /// 禁用设备拒绝；未知设备可连接但尚未创建台账.
    /// </summary>
    [Fact]
    public async Task Broker_DisabledRejected_UnknownConnectDoesNotRegister()
    {
        using var connection = CreateDeviceDatabase(("dev-real", true), ("dev-off", false));
        var options = BuildOptions(connection);
        using var provider = CreateProvider(options);
        var checker = provider.GetRequiredService<DeviceCredentialService>();

        var buffer = new RecordingBuffer();
        using var broker = await StartBrokerAsync(buffer, checker, provider.GetRequiredService<IMqttDeviceRegistry>());

        var disabled = await TryConnectAsync(broker.BoundPort!.Value, "dev-off", string.Empty);
        Assert.Equal(MqttClientConnectResultCode.BadUserNameOrPassword, disabled.ResultCode);

        var unknownDevice = await TryConnectAsync(broker.BoundPort!.Value, "ghost", string.Empty);
        Assert.Equal(MqttClientConnectResultCode.Success, unknownDevice.ResultCode);
        using var scope = provider.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<DatabaseContext>().Devices.AnyAsync(x => x.DeviceKey == "ghost"));
        Assert.Empty(buffer.Telemetry);
    }

    /// <summary>
    /// CheckAsync 直测：存在且启用无需密钥 → Ok；禁用或删除设备 → Fail.
    /// </summary>
    [Fact]
    public async Task CheckAsync_EnabledAndDisabledDevice_ResultMapped()
    {
        using var connection = CreateDeviceDatabase(("dev-ok", true), ("dev-off", false));
        var options = BuildOptions(connection);
        using var provider = CreateProvider(options);
        var checker = provider.GetRequiredService<DeviceCredentialService>();

        var ok = await checker.CheckAsync("dev-ok", string.Empty, CancellationToken.None);
        Assert.True(ok.Success);
        Assert.Equal("dev-ok", ok.DeviceKey);

        var disabled = await checker.CheckAsync("dev-off", string.Empty, CancellationToken.None);
        Assert.False(disabled.Success);
        Assert.NotNull(disabled.Reason);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        db.Devices.Remove(await db.Devices.SingleAsync(x => x.DeviceKey == "dev-ok"));
        await db.SaveChangesAsync();
        Assert.True((await checker.CheckAsync("dev-ok", string.Empty, CancellationToken.None)).Success);
    }

    /// <summary>
    /// 建内存 SQLite 库并写入无密钥设备行，审计字段由 DatabaseContext 自动填充.
    /// </summary>
    private static SqliteConnection CreateDeviceDatabase(params (string Key, bool Enabled)[] devices)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = new DatabaseContext(BuildOptions(connection));
        db.Database.EnsureCreated();
        foreach (var (key, enabled) in devices)
        {
            db.Devices.Add(new DeviceEntity
            {
                DeviceKey = key,
                Name = key,
                Enabled = enabled,
            });
        }

        db.SaveChanges();
        return connection;
    }

    private static ServiceProvider CreateProvider(DbContextOptions<DatabaseContext> options)
    {
        var services = new ServiceCollection();
        services.AddScoped<DatabaseContext>(_ => new DatabaseContext(options));
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<DeviceLastSeenBuffer>();
        services.AddSingleton<DeviceCredentialService>();
        services.AddSingleton<IIdProvider, TestIds>();
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<MqttDeviceRegistry>>(NullLogger<MqttDeviceRegistry>.Instance);
        services.AddSingleton<IMqttDeviceRegistry, MqttDeviceRegistry>();
        services.AddSingleton<ISystemSettingsProvider, AuthSettings>();
        return services.BuildServiceProvider();
    }

    private sealed class TestIds : IIdProvider
    {
        private long _next = 1000;

        public long GetLongId() => Interlocked.Increment(ref _next);
    }

    private sealed class AuthSettings : ISystemSettingsProvider
    {
        public MqttAuthOptions Auth { get; set; } = new();
        public MqttAuthOptions GetMqttAuth() => Auth;
        public DuckDbSettings GetDuckDb() => new();
        public S3Options GetS3() => new();
        public RetentionPlanOptions GetRetention() => new();
        public CompressionOptions GetCompression() => new();
        public List<MqInstanceOptions> GetMqInstances() => [];
        public OtlpOptions GetOtlp() => new();
        public Task SaveAsync(string key, string json, CancellationToken token) => Task.CompletedTask;
    }

    [Fact]
    public async Task Broker_AuthModes_ApplyToNewConnections()
    {
        using var connection = CreateDeviceDatabase(("dev-real", true));
        using var provider = CreateProvider(BuildOptions(connection));
        var settings = (AuthSettings)provider.GetRequiredService<ISystemSettingsProvider>();
        var checker = provider.GetRequiredService<DeviceCredentialService>();
        using var broker = await StartBrokerAsync(new RecordingBuffer(), checker, provider.GetRequiredService<IMqttDeviceRegistry>());
        var key = "test-" + Guid.NewGuid().ToString("N");
        settings.Auth = new() { Mode = "shared", KeyHash = new Pbkdf2PasswordHasher().Hash(key), PrefixLength = key.Length };
        using (var client = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev-real", key)) Assert.True(client.IsConnected);
        Assert.Equal(MqttClientConnectResultCode.BadUserNameOrPassword, (await TryConnectAsync(broker.BoundPort!.Value, "dev-real", key + "suffix")).ResultCode);
        settings.Auth = new() { Mode = "prefix", KeyHash = new Pbkdf2PasswordHasher().Hash(key), PrefixLength = key.Length };
        using (var client = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev-real", key + "suffix")) Assert.True(client.IsConnected);
        Assert.False(await checker.ValidateAsync("dev-real", key.ToUpperInvariant()));
        settings.Auth = new() { Mode = "prefix", PrefixLength = 0, KeyHash = "invalid" };
        Assert.False(await checker.ValidateAsync("dev-real", key));
        settings.Auth = new() { Mode = "shared", PrefixLength = 1, KeyHash = "pbkdf2$bad$bad$bad" };
        Assert.False(await checker.ValidateAsync("dev-real", key));
        settings.Auth = new();
        using (var client = await ConnectDeviceAsync(broker.BoundPort!.Value, "dev-real", string.Empty)) Assert.True(client.IsConnected);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("shared")]
    [InlineData("prefix")]
    public async Task Broker_FirstValidPublish_RegistersOnceAndFlushesLastSeen(string mode)
    {
        using var connection = CreateDeviceDatabase();
        using var provider = CreateProvider(BuildOptions(connection));
        var key = "demo-key";
        ((AuthSettings)provider.GetRequiredService<ISystemSettingsProvider>()).Auth = new()
        {
            Mode = mode,
            KeyHash = new Pbkdf2PasswordHasher().Hash(key),
            PrefixLength = key.Length,
        };
        var buffer = new RecordingBuffer();
        using var broker = await StartBrokerAsync(buffer, provider.GetRequiredService<DeviceCredentialService>(),
            provider.GetRequiredService<IMqttDeviceRegistry>());
        using var client = await ConnectDeviceAsync(broker.BoundPort!.Value, "demo-new", mode == "prefix" ? key + "-suffix" : key);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        Assert.Empty(await db.Devices.ToListAsync());

        // ACL 和完整 payload 校验必须先于注册.
        Assert.Equal(MqttClientPublishReasonCode.NotAuthorized, (await PublishAsync(client, "iot/other/temperature", """{"v":1}""")).ReasonCode);
        Assert.Equal(MqttClientPublishReasonCode.PayloadFormatInvalid, (await PublishAsync(client, "iot/demo-new/temperature", "invalid")).ReasonCode);
        Assert.Empty(await db.Devices.ToListAsync());

        var before = DateTimeOffset.UtcNow;
        Assert.True((await PublishAsync(client, "iot/demo-new/temperature", """{"v":42,"ts":"2000-01-01T00:00:00Z"}""")).IsSuccess);
        var device = await db.Devices.SingleAsync();
        Assert.True(device.Id > 1000);
        Assert.Equal("demo-new", device.Name);
        Assert.True(device.Enabled);
        Assert.Equal(string.Empty, device.Group);
        Assert.Equal("{}", device.TagsJson);
        Assert.Equal(0, device.CreateUserId);
        Assert.True(device.CreateTime >= before);
        device.Name = "保留名称";
        device.Group = "保留分组";
        device.TagsJson = """{"site":"demo"}""";
        await db.SaveChangesAsync();
        Assert.True((await PublishAsync(client, "iot/demo-new/temperature", """{"v":43}""")).IsSuccess);
        Assert.Equal(2, buffer.Telemetry.Count);
        Assert.Single(await db.Devices.ToListAsync());
        Assert.Equal("保留名称", (await db.Devices.AsNoTracking().SingleAsync()).Name);

        var lastSeen = provider.GetRequiredService<DeviceLastSeenBuffer>();
        Assert.Equal(1, await DeviceLastSeenFlusher.FlushAsync(db, lastSeen.Drain()));
        Assert.True((await db.Devices.AsNoTracking().SingleAsync()).LastSeenTime >= before);
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("prefix")]
    public async Task Broker_UnknownWrongKey_DoesNotRegister(string mode)
    {
        using var connection = CreateDeviceDatabase();
        using var provider = CreateProvider(BuildOptions(connection));
        ((AuthSettings)provider.GetRequiredService<ISystemSettingsProvider>()).Auth = new()
        {
            Mode = mode,
            KeyHash = new Pbkdf2PasswordHasher().Hash("valid-key"),
            PrefixLength = 9,
        };
        using var broker = await StartBrokerAsync(new RecordingBuffer(), provider.GetRequiredService<DeviceCredentialService>(),
            provider.GetRequiredService<IMqttDeviceRegistry>());
        Assert.Equal(MqttClientConnectResultCode.BadUserNameOrPassword,
            (await TryConnectAsync(broker.BoundPort!.Value, "unknown", "wrong-key")).ResultCode);
        using var scope = provider.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<DatabaseContext>().Devices.ToListAsync());
    }

    [Theory]
    [InlineData("bad/key")]
    [InlineData("bad+key")]
    [InlineData("bad#key")]
    [InlineData("bad\n")]
    public async Task CheckAsync_InvalidKey_Rejected(string deviceKey)
    {
        using var connection = CreateDeviceDatabase();
        using var provider = CreateProvider(BuildOptions(connection));
        Assert.False(await provider.GetRequiredService<DeviceCredentialService>().ValidateAsync(deviceKey, string.Empty));
    }

    [Fact]
    public async Task Registry_ConcurrentFirstPush_OneRow_DeletedRegistersAgain_DisabledStaysBlocked()
    {
        using var connection = CreateDeviceDatabase(("off", false), ("deleted", true));
        using var provider = CreateProvider(BuildOptions(connection));
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        db.Devices.Remove(await db.Devices.SingleAsync(x => x.DeviceKey == "deleted"));
        await db.SaveChangesAsync();
        var registry = provider.GetRequiredService<IMqttDeviceRegistry>();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => registry.EnsureAsync("concurrent", CancellationToken.None)));
        Assert.All(results, Assert.True);
        Assert.Single(await db.Devices.Where(x => x.DeviceKey == "concurrent").ToListAsync());
        Assert.False(await registry.EnsureAsync("off", CancellationToken.None));
        Assert.True(await registry.EnsureAsync("deleted", CancellationToken.None));
        Assert.True(await provider.GetRequiredService<DeviceCredentialService>().ValidateAsync("deleted", string.Empty));
        Assert.True((await db.Devices.AsNoTracking().SingleAsync(x => x.DeviceKey == "deleted")).Id > 1000);
        Assert.Equal(3, await db.Devices.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Broker_OnlineDeleteRegistersNewId_OnlineDisableBlocksAllSessions()
    {
        using var connection = CreateDeviceDatabase();
        using var provider = CreateProvider(BuildOptions(connection));
        var registry = provider.GetRequiredService<IMqttDeviceRegistry>();
        var buffer = new RecordingBuffer();
        using var broker = await StartBrokerAsync(buffer, provider.GetRequiredService<DeviceCredentialService>(), registry);
        using var first = await ConnectDeviceAsync(broker.BoundPort!.Value, "online", string.Empty);
        using var second = await ConnectDeviceAsync(broker.BoundPort!.Value, "online", string.Empty);
        const string topic = "iot/online/temperature";
        const string payload = """{"v":1}""";
        Assert.True((await PublishAsync(first, topic, payload)).IsSuccess);
        Assert.True((await PublishAsync(second, topic, payload)).IsSuccess);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var original = await db.Devices.AsNoTracking().SingleAsync();
        var save = new SaveDeviceCommandHandler(db, provider.GetRequiredService<IIdProvider>(), registry);
        await save.Handle(new SaveDeviceCommand
        {
            Id = original.Id, DeviceKey = "online", Name = "停用设备", Enabled = false,
        }, CancellationToken.None);
        Assert.Equal(MqttClientPublishReasonCode.NotAuthorized, (await PublishAsync(first, topic, payload)).ReasonCode);
        Assert.Equal(MqttClientPublishReasonCode.NotAuthorized, (await PublishAsync(second, topic, payload)).ReasonCode);
        Assert.Equal(2, buffer.Telemetry.Count);
        Assert.False((await db.Devices.AsNoTracking().SingleAsync()).Enabled);

        await new DeleteDeviceCommandHandler(db, registry).Handle(new DeleteDeviceCommand { Id = original.Id }, CancellationToken.None);
        Assert.Empty(await db.Devices.IgnoreQueryFilters().AsNoTracking().ToListAsync());
        Assert.True((await PublishAsync(first, topic, payload)).IsSuccess);
        var replacement = await db.Devices.AsNoTracking().SingleAsync();
        Assert.NotEqual(original.Id, replacement.Id);
        Assert.True(replacement.Enabled);
        Assert.Equal("online", replacement.Name);
        Assert.True((await PublishAsync(second, topic, payload)).IsSuccess);
        Assert.Single(await db.Devices.IgnoreQueryFilters().AsNoTracking().ToListAsync());
        Assert.Equal(4, buffer.Telemetry.Count);
    }

    private static Task<MqttClientPublishResult> PublishAsync(IMqttClient client, string topic, string payload) =>
        client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());

    private static DbContextOptions<DatabaseContext> BuildOptions(SqliteConnection connection)
    {
        return new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(connection).Options;
    }

    private static async Task<MqttBrokerService> StartBrokerAsync(IIngestBuffer buffer, IDeviceCredentialChecker? checker, IMqttDeviceRegistry registry)
    {
        var broker = new MqttBrokerService(
            BuildConfiguration(GetFreePort()),
            buffer,
            checker == null ? Array.Empty<IDeviceCredentialChecker>() : new IDeviceCredentialChecker[] { checker },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MqttBrokerService>.Instance, registry);
        await broker.StartAsync(CancellationToken.None);
        await WaitForBoundAsync(broker);

        return broker;
    }

    private static IConfiguration BuildConfiguration(int port)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SData:Mqtt:Port"] = port.ToString(),
        }).Build();
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
    /// 一次性连接尝试（被拒时返回非 Success 结果码，不抛异常）.
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
    /// 测试用记录缓冲：仅记录遥测（容量默认无限）.
    /// </summary>
    private sealed class RecordingBuffer : IIngestBuffer
    {
        private readonly object _lock = new();

        public List<TelemetryRecord> Telemetry { get; } = new();

        public bool TryEnqueueTelemetry(TelemetryRecord record)
        {
            lock (_lock)
            {
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
