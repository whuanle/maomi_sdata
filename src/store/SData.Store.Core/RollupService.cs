using DuckDB.NET.Data;
using Microsoft.Extensions.Hosting;
using SData.Infra.Configuration;

namespace SData.Store;

/// <summary>
/// 指标 rollup 回填后台服务（契约 C6）：周期把原始 metrics 天表聚合进 metrics_rollup_5m/1h 全局汇总表.
/// 每轮回填「最近已关闭的」桶窗口（先 DELETE 再 INSERT，天然幂等，重启无状态）；
/// 5m 表回填最近 60 分钟（滞后 ≤10 分钟），1h 表回填最近 4 小时（滞后 ≤2 小时）.
/// DDL 由 DuckDbSchemaBootstrapper 内嵌 002_rollup.sql 统一应用（幂等），本服务只负责回填.
/// </summary>
public class RollupService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Window5m = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan Window1h = TimeSpan.FromHours(4);

    private readonly DuckDbConnectionManager _connectionManager;
    private readonly ILogger<RollupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RollupService"/> class.
    /// </summary>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="logger">日志.</param>
    public RollupService(DuckDbConnectionManager connectionManager, ILogger<RollupService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Metric rollup service started (5m window={Window5m}, 1h window={Window1h})", Window5m, Window1h);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken);
                await BackfillAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _connectionManager.LogResourceFailure(exception, "rollup");
                _logger.LogError(exception, "Metric rollup backfill failed");
            }
        }

        _logger.LogInformation("Metric rollup service stopped");
    }

    /// <summary>
    /// 单轮回填（5m 与 1h 各一个窗口；内部方法供测试直调）.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    internal async Task BackfillAsync(CancellationToken cancellationToken)
    {
        using var sourceLease = await _connectionManager.ReadSourcesAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await BackfillWindowAsync(TimeTables.Rollup5mTable, TimeSpan.FromMinutes(5), "5 minutes", Window5m, now, cancellationToken);
        await BackfillWindowAsync(TimeTables.Rollup1hTable, TimeSpan.FromHours(1), "1 hour", Window1h, now, cancellationToken);
    }

    private async Task BackfillWindowAsync(string table, TimeSpan step, string stepSql, TimeSpan window, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var to = FloorToStep(now, step);
        var from = to - window;
        var tables = _connectionManager.GetDayTables(TimeTables.MetricsPrefix, from, to);
        if (tables.Count == 0)
        {
            return;
        }

        // 每桶单独计算与提交，限制中间聚合规模；计算失败不删除原汇总。
        for (var bucket = from; bucket < to; bucket += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var queryLease = await _connectionManager.LockQueryAsync(cancellationToken);
            using var connection = _connectionManager.CloneConnection();
            using var command = connection.CreateCommand();
            command.Parameters.Add(new DuckDBParameter("from", LogStore.ToDb(bucket)));
            command.Parameters.Add(new DuckDBParameter("to", LogStore.ToDb(bucket + step)));
            var union = string.Join(" UNION ALL ", tables.Select(t => $"SELECT name, labels, ts, value FROM {t} WHERE ts >= $from AND ts < $to"));
            command.CommandText = $"""
                CREATE TEMP TABLE sdata_rollup_stage AS
                SELECT name, md5(coalesce(labels, '')) AS labels_hash, labels,
                       time_bucket(INTERVAL '{stepSql}', ts) AS bucket_ts,
                       min(value) AS min, max(value) AS max, sum(value) AS sum, count(*) AS count
                FROM ({union})
                GROUP BY ALL
                """;
            await using var timer = new Timer(_ =>
            {
                if (cancellationToken.IsCancellationRequested) command.Cancel();
            }, null, 50, 50);
            using var writeLease = await _connectionManager.LockWriteAsync(cancellationToken);
            await command.ExecuteNonQueryAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            command.CommandText = $"DELETE FROM {table} WHERE bucket_ts >= $from AND bucket_ts < $to";
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.Parameters.Clear();
            command.CommandText = $"INSERT INTO {table} SELECT * FROM sdata_rollup_stage";
            await command.ExecuteNonQueryAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
    }

    private static DateTimeOffset FloorToStep(DateTimeOffset time, TimeSpan step)
    {
        var ticks = time.ToUniversalTime().UtcDateTime.Ticks;
        return new DateTimeOffset(ticks - (ticks % step.Ticks), TimeSpan.Zero);
    }
}
