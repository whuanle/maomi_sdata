using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Services;

namespace SData.Iot.Services;

/// <summary>
/// 单机首次注册串行化，唯一索引兜底与手工创建设备的竞争.
/// </summary>
public sealed class MqttDeviceRegistry(
    IServiceScopeFactory scopeFactory,
    IIdProvider idProvider,
    DeviceLastSeenBuffer lastSeen,
    ILogger<MqttDeviceRegistry> logger) : IMqttDeviceRegistry, IDisposable
{
    private readonly SemaphoreSlim _registrationLock = new(1, 1);
    private long _version;

    /// <inheritdoc/>
    public long Version => Interlocked.Read(ref _version);

    /// <inheritdoc/>
    public void Invalidate() => Interlocked.Increment(ref _version);

    /// <inheritdoc/>
    public async Task<bool> EnsureAsync(string deviceKey, CancellationToken cancellationToken)
    {
        if (!DeviceKeyRules.IsValid(deviceKey))
        {
            return false;
        }

        await _registrationLock.WaitAsync(cancellationToken);
        try
        {
            try
            {
                return await RegisterAsync(deviceKey, cancellationToken);
            }
            catch (DbUpdateException exception) when (IsUniqueConflict(exception))
            {
                // 原事务和作用域已释放，使用全新上下文重读并发创建的赢家.
                await using var scope = scopeFactory.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
                var winner = await database.Devices.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.DeviceKey == deviceKey, cancellationToken);
                if (winner == null)
                {
                    throw;
                }

                return winner.Enabled;
            }
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    /// <inheritdoc/>
    public void Touch(string deviceKey, DateTimeOffset receivedAt) => lastSeen.Touch(deviceKey, receivedAt);

    /// <inheritdoc/>
    public void Dispose() => _registrationLock.Dispose();

    private async Task<bool> RegisterAsync(string deviceKey, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var existing = await database.Devices.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.DeviceKey == deviceKey, cancellationToken);
        if (existing != null && existing.IsDeleted == 0)
        {
            return existing.Enabled;
        }

        if (existing != null)
        {
            // 兼容历史软删行：清理墓碑再创建新 ID，不恢复旧资料.
            await database.Devices.IgnoreQueryFilters()
                .Where(x => x.Id == existing.Id && x.IsDeleted != 0)
                .ExecuteDeleteAsync(cancellationToken);
        }

        database.Devices.Add(new DeviceEntity
        {
            Id = idProvider.GetLongId(),
            DeviceKey = deviceKey,
            Name = deviceKey,
            Enabled = true,
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("MQTT 有效发布自动注册设备 device={DeviceKey}", deviceKey);
        return true;
    }

    private static bool IsUniqueConflict(DbUpdateException exception) =>
        exception.InnerException is Microsoft.Data.Sqlite.SqliteException
        { SqliteExtendedErrorCode: 2067 or 1555 };
}
