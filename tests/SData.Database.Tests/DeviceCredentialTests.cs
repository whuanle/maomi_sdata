using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Database.Entities;
using SData.Database.Sqlite;
using SData.Infra.Services;
using SData.Iot.Services;
using SData.Settings;
using SData.Infra.Configuration;

namespace SData.Database.Tests;

/// <summary>
/// 设备免密码接入与 LastSeen 批量回写测试.
/// </summary>
public class DeviceCredentialTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ServiceProvider _provider;
    private readonly SqliteDatabaseContext _dbContext;
    private readonly IPasswordHasher _passwordHasher = new Pbkdf2PasswordHasher();
    private readonly DeviceLastSeenBuffer _buffer = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceCredentialTests"/> class.
    /// </summary>
    public DeviceCredentialTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sdata-devcred-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        var services = new ServiceCollection();
        services.AddScoped<DatabaseContext>(_ => new SqliteDatabaseContext(options));
        services.AddSingleton<ISystemSettingsProvider, AuthSettings>();
        _provider = services.BuildServiceProvider();
        _dbContext = new SqliteDatabaseContext(options);
        _dbContext.Database.EnsureCreated();
        SchemaReconciler.Apply(_dbContext, NullLogger.Instance);

        _dbContext.Devices.Add(new DeviceEntity
        {
            Id = 300,
            DeviceKey = "dev-cred-1",
            Name = "凭证设备",
            SecretHash = _passwordHasher.Hash(Guid.NewGuid().ToString("N")),
        });
        _dbContext.Devices.Add(new DeviceEntity
        {
            Id = 301,
            DeviceKey = "dev-nosecret",
            Name = "未设密钥设备",
            SecretHash = string.Empty,
        });
        _dbContext.Devices.Add(new DeviceEntity
        {
            Id = 302,
            DeviceKey = "dev-disabled",
            Name = "禁用设备",
            Enabled = false,
            SecretHash = string.Empty,
        });
        _dbContext.SaveChanges();
    }

    private sealed class AuthSettings : ISystemSettingsProvider
    {
        public MqttAuthOptions GetMqttAuth() => new();
        public S3Options GetS3() => new();
        public RetentionPlanOptions GetRetention() => new();
        public CompressionOptions GetCompression() => new();
        public List<MqInstanceOptions> GetMqInstances() => [];
        public OtlpOptions GetOtlp() => new();
        public Task SaveAsync(string key, string json, CancellationToken token) => Task.CompletedTask;
    }

    private DeviceCredentialService CreateService()
    {
        return new DeviceCredentialService(_passwordHasher, _provider.GetRequiredService<IServiceScopeFactory>(), _buffer);
    }

    /// <summary>
    /// 免密码允许合法未知设备连接但不创建，禁用设备仍拒绝，与旧哈希无关.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_AcceptsRegisteredEnabledDevicesWithoutPassword()
    {
        var service = CreateService();
        Assert.True(await service.ValidateAsync("dev-cred-1", string.Empty));
        Assert.True(await service.ValidateAsync("dev-nosecret", string.Empty));
        Assert.False(await service.ValidateAsync("dev-disabled", string.Empty));
        Assert.True(await service.ValidateAsync("dev-missing", string.Empty));
        Assert.False(await _dbContext.Devices.AnyAsync(x => x.DeviceKey == "dev-missing"));
    }

    /// <summary>
    /// 兼容旧哈希工具，但免密码接入不依赖旧密钥.
    /// </summary>
    [Fact]
    public void HashSecret_Pbkdf2Format_AndRoundTrips()
    {
        var secret = Guid.NewGuid().ToString("N");
        var hash = CreateService().HashSecret(secret);
        Assert.StartsWith("pbkdf2$", hash, StringComparison.Ordinal);
        Assert.True(_passwordHasher.Verify(secret, hash));
        Assert.False(_passwordHasher.Verify(Guid.NewGuid().ToString("N"), hash));
    }

    /// <summary>
    /// LastSeen 内存累积（同设备取最新）后批量回写（单事务批量 UPDATE，非逐消息写）.
    /// </summary>
    [Fact]
    public async Task LastSeenFlush_BatchUpdatesDevices()
    {
        var now = DateTimeOffset.UtcNow;

        // 同设备两次 Touch 取最新；含一个不存在的设备（回写计数不含它）
        _buffer.Touch("dev-cred-1", now.AddSeconds(-30));
        _buffer.Touch("dev-cred-1", now);
        _buffer.Touch("dev-missing", now);
        Assert.Equal(0, await _dbContext.Devices.CountAsync(x => x.LastSeenTime != null));
        Assert.Equal(2, _buffer.Count);

        var batch = _buffer.Drain();
        Assert.Equal(2, batch.Count);
        Assert.Equal(0, _buffer.Count); // Drain 后缓冲清空

        var flushed = await DeviceLastSeenFlusher.FlushAsync(_dbContext, batch);

        Assert.Equal(1, flushed); // 仅存在的设备计入更新数
        var device = await _dbContext.Devices.AsNoTracking().SingleAsync(x => x.DeviceKey == "dev-cred-1");
        Assert.NotNull(device.LastSeenTime);
        Assert.True(Math.Abs((device.LastSeenTime!.Value - now).TotalSeconds) < 1);
        Assert.Equal(0, _buffer.Count);
    }

    /// <summary>
    /// 二次批量回写覆盖旧值.
    /// </summary>
    [Fact]
    public async Task LastSeenFlush_SecondBatchOverwrites()
    {
        var first = DateTimeOffset.UtcNow.AddMinutes(-5);
        var second = DateTimeOffset.UtcNow;

        _buffer.Touch("dev-cred-1", first);
        await DeviceLastSeenFlusher.FlushAsync(_dbContext, _buffer.Drain());

        _buffer.Touch("dev-cred-1", second);
        await DeviceLastSeenFlusher.FlushAsync(_dbContext, _buffer.Drain());

        var device = await _dbContext.Devices.AsNoTracking().SingleAsync(x => x.DeviceKey == "dev-cred-1");
        Assert.True(Math.Abs((device.LastSeenTime!.Value - second).TotalSeconds) < 1);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _dbContext.Dispose();
        _provider.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }
}
