using Mediator;
using SData.Infra.Configuration;
using SData.Ingest;
using SData.Settings;
using SData.Store;
using SData.Store.Core;

namespace SData.Monitor.Handlers;

/// <summary>
/// 总览页聚合处理：通道健康（缓冲统计）/ 今日量（天表行数）/ 存储用量（热层 DuckDB 文件）.
/// </summary>
public class QueryOverviewCommandHandler : IRequestHandler<QueryOverviewCommand, OverviewResponse>
{
    private static readonly TimeSpan TodayRowsCacheTtl = TimeSpan.FromSeconds(5);
    private static readonly (string Prefix, string Name, string StatsKey)[] Domains =
    {
        (TimeTables.LogsPrefix, "logs", "log"),
        (TimeTables.TracesPrefix, "traces", "trace"),
        (TimeTables.MetricsPrefix, "metrics", "metric"),
        (TimeTables.IotPrefix, "iot", "telemetry"),
        (TimeTables.RumEventPrefix, "rum_event", "rumevent"),
        (TimeTables.RumSessionPrefix, "rum_session", "rumsession"),
        (TimeTables.RumReplayPrefix, "rum_replay", "rumreplay"),
    };

    private readonly IIngestBuffer _buffer;
    private readonly IStoreInfo _storeInfo;
    private readonly SystemOptions _systemOptions;
    private readonly ISystemSettingsProvider _settings;
    private readonly IStorageMetrics _metrics;
    private readonly DuckDbConnectionManager _duckDb;
    private readonly SemaphoreSlim _todayRowsCacheLock = new(1, 1);
    private DateTimeOffset _todayRowsCacheDate;
    private DateTimeOffset _todayRowsCacheExpiresAt;
    private IReadOnlyDictionary<string, long>? _todayRowsCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryOverviewCommandHandler"/> class.
    /// </summary>
    /// <param name="buffer">接入缓冲.</param>
    /// <param name="storeInfo">存储信息.</param>
    /// <param name="systemOptions">系统配置.</param>
    /// <param name="settings">有效运行配置.</param>
    public QueryOverviewCommandHandler(IIngestBuffer buffer, IStoreInfo storeInfo, SystemOptions systemOptions, ISystemSettingsProvider settings, IStorageMetrics metrics, DuckDbConnectionManager duckDb)
    {
        _buffer = buffer;
        _storeInfo = storeInfo;
        _systemOptions = systemOptions;
        _settings = settings;
        _metrics = metrics;
        _duckDb = duckDb;
    }

    /// <inheritdoc/>
    public async ValueTask<OverviewResponse> Handle(QueryOverviewCommand request, CancellationToken cancellationToken)
    {
        var response = new OverviewResponse
        {
            Storage = BuildStorage(),
            LocalResources = _metrics.Local,
            S3Resources = _metrics.S3,
        };

        var runtime = MqRuntime.Snapshot().ToDictionary(x => x.InstanceId);
        response.MqConsumers = _settings.GetMqInstances().Select(instance =>
        {
            runtime.TryGetValue(instance.Id, out var item);
            return new OverviewMqConsumer
            {
                InstanceId = instance.Id,
                Name = instance.Name,
                Type = instance.Type,
                Enabled = instance.Enabled,
                State = instance.Enabled ? item?.State ?? "unknown" : "disabled",
                PersistedRecords = item?.PersistedRecords ?? 0,
                LastPersistedAt = item?.LastPersistedAt,
                // 原始客户端异常可能含 URI、口令；总览只返回安全提示。
                LastError = string.IsNullOrEmpty(item?.LastError) ? null : "消费出现异常，请联系管理员查看服务日志",
            };
        }).ToList();

        var stats = _buffer.GetStats();
        response.BufferedRecords = stats.Buffered.Values.DefaultIfEmpty(0).Max();
        var today = DateTimeOffset.UtcNow;

        // 一次性批量统计各域今日行数：原先每域各占一次查询门 + 克隆一次连接，这里合并为单条 UNION ALL
        var todayTables = new List<(string Name, string TableName)>(Domains.Length);
        foreach (var (prefix, name, statsKey) in Domains)
        {
            var tables = _storeInfo.GetTableNames(prefix);
            response.TableCounts[name] = tables.Count;
            todayTables.Add((name, TimeTables.Name(prefix, today)));

            // 缓冲统计键为 IngestKind 枚举小写（log/trace/metric/telemetry/rumevent/rumsession/rumreplay）
            response.Channels.Add(new OverviewChannel
            {
                Name = name,
                Processed = stats.Processed.GetValueOrDefault(statsKey),
                Dropped = stats.Dropped.GetValueOrDefault(statsKey),
                Buffered = stats.Buffered.GetValueOrDefault(statsKey),
            });
        }

        var todayRows = await GetTodayRowsAsync(todayTables, today, cancellationToken);
        foreach (var (name, rows) in todayRows)
        {
            response.TodayRows[name] = rows;
        }

        return response;
    }

    private async Task<IReadOnlyDictionary<string, long>> GetTodayRowsAsync(
        IReadOnlyList<(string Name, string TableName)> todayTables,
        DateTimeOffset today,
        CancellationToken cancellationToken)
    {
        var date = today.UtcDateTime.Date;
        var now = DateTimeOffset.UtcNow;
        if (_todayRowsCache != null
            && _todayRowsCacheDate.UtcDateTime.Date == date
            && _todayRowsCacheExpiresAt > now)
        {
            return _todayRowsCache;
        }

        await _todayRowsCacheLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_todayRowsCache != null
                && _todayRowsCacheDate.UtcDateTime.Date == date
                && _todayRowsCacheExpiresAt > now)
            {
                return _todayRowsCache;
            }

            var result = await _storeInfo.CountTablesAsync(todayTables, cancellationToken);
            var snapshot = new Dictionary<string, long>(result, StringComparer.Ordinal);
            _todayRowsCache = snapshot;
            _todayRowsCacheDate = today;
            _todayRowsCacheExpiresAt = now + TodayRowsCacheTtl;
            return snapshot;
        }
        finally
        {
            _todayRowsCacheLock.Release();
        }
    }

    private OverviewStorage BuildStorage()
    {
        var s3 = _settings.GetS3();
        var storage = new OverviewStorage
        {
            ColdEnabled = !string.IsNullOrWhiteSpace(s3.Endpoint) && !string.IsNullOrWhiteSpace(s3.Bucket),
            MemoryLimit = _duckDb.EffectiveMemoryLimit,
            HotDbPath = _systemOptions.DuckDb.Path,
        };

        try
        {
            var dbPath = Path.Combine(SData.Infra.AppConst.GetPath(), _systemOptions.DuckDb.Path.Replace('\\', '/'));
            var fileInfo = new FileInfo(dbPath);
            if (fileInfo.Exists)
            {
                storage.HotDbBytes = fileInfo.Length;
            }
        }
        catch (IOException)
        {
            // 文件系统不可达时保持 0，总览聚合不因存储探测失败而整体失败
        }

        return storage;
    }
}
