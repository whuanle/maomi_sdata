using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SData.Database;

namespace SData.Iot.Services;

/// <summary>
/// 设备 LastSeen 内存累积器（MQTT 上报线程调用 Touch，Flusher 周期 Drain）.
/// </summary>
public class DeviceLastSeenBuffer
{
    private ConcurrentDictionary<string, DateTimeOffset> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// 记录设备活跃时间（同设备取最新）.
    /// </summary>
    /// <param name="deviceKey">设备标识.</param>
    /// <param name="time">上报时间.</param>
    public void Touch(string deviceKey, DateTimeOffset time)
    {
        _pending.AddOrUpdate(deviceKey, time, (_, existing) => time > existing ? time : existing);
    }

    /// <summary>
    /// 取走全部待回写项（原子清空）.
    /// </summary>
    /// <returns>待回写快照.</returns>
    public IReadOnlyDictionary<string, DateTimeOffset> Drain()
    {
        var current = Interlocked.Exchange(ref _pending, new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal));
        return current;
    }

    /// <summary>
    /// 当前累积条数.
    /// </summary>
    public int Count => _pending.Count;
}

/// <summary>
/// 设备 LastSeen 批量回写后台服务：每 30 秒把内存累积一次性批量 UPDATE 到 SQLite（禁止逐消息写）.
/// </summary>
public class DeviceLastSeenFlusher : BackgroundService
{
    private readonly DeviceLastSeenBuffer _buffer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeviceLastSeenFlusher> _logger;

    /// <summary>
    /// 批量回写周期.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceLastSeenFlusher"/> class.
    /// </summary>
    /// <param name="buffer">LastSeen 内存累积器.</param>
    /// <param name="scopeFactory">服务作用域工厂（每次回写取独立 DatabaseContext）.</param>
    /// <param name="logger">日志.</param>
    public DeviceLastSeenFlusher(DeviceLastSeenBuffer buffer, IServiceScopeFactory scopeFactory, ILogger<DeviceLastSeenFlusher> logger)
    {
        _buffer = buffer;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                var batch = _buffer.Drain();
                if (batch.Count == 0)
                {
                    continue;
                }

                await using var scope = _scopeFactory.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
                var flushed = await FlushAsync(context, batch, stoppingToken);
                _logger.LogDebug("Device LastSeen flushed {Count} devices", flushed);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Device LastSeen flush failed");
            }
        }
    }

    /// <summary>
    /// 批量回写一批 LastSeen（参数化 UPDATE，单事务，可在测试中直接调用）.
    /// </summary>
    /// <param name="context">SQLite 元数据库.</param>
    /// <param name="batch">设备标识 → 最近活跃时间.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>回写条数.</returns>
    public static async Task<int> FlushAsync(DatabaseContext context, IReadOnlyDictionary<string, DateTimeOffset> batch, CancellationToken cancellationToken = default)
    {
        var flushed = 0;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var pair in batch)
            {
                flushed += await context.Devices
                    .Where(x => x.DeviceKey == pair.Key)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastSeenTime, pair.Value), cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return flushed;
        }
        catch
        {
            // 失败整批回滚：调用方（ExecuteAsync）记日志后丢弃本批，等待下一次上报重新累积
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
