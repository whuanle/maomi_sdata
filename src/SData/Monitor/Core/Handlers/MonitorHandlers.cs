using System.Diagnostics;
using MediatR;
using SData.Infra.Configuration;
using SData.Store;
using SData.Settings;
using SData.Store.Core;

namespace SData.Monitor.Handlers;

/// <summary>
/// 服务状态查询处理.
/// </summary>
public class QueryStatusCommandHandler : IRequestHandler<QueryStatusCommand, StatusResponse>
{
    private readonly IIngestBuffer _buffer;
    private readonly IStoreInfo _storeInfo;
    private readonly SystemOptions _systemOptions;
    private readonly ResourceSampler _resources;
    private readonly IDuckDbRuntimeSettings _duckDb;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryStatusCommandHandler"/> class.
    /// </summary>
    /// <param name="buffer">接入缓冲.</param>
    /// <param name="storeInfo">存储信息.</param>
    /// <param name="systemOptions">系统配置.</param>
    public QueryStatusCommandHandler(IIngestBuffer buffer, IStoreInfo storeInfo, SystemOptions systemOptions, ResourceSampler resources, IDuckDbRuntimeSettings duckDb)
    {
        _buffer = buffer;
        _storeInfo = storeInfo;
        _systemOptions = systemOptions;
        _resources = resources;
        _duckDb = duckDb;
    }

    /// <inheritdoc/>
    public async Task<StatusResponse> Handle(QueryStatusCommand request, CancellationToken cancellationToken)
    {
        using var process = Process.GetCurrentProcess();
        var response = new StatusResponse
        {
            MqConsumers = SData.Ingest.MqRuntime.Snapshot(),
            Resources = _resources.Snapshot,
            Name = _systemOptions.Name,
            Version = typeof(QueryStatusCommandHandler).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            UptimeSeconds = (long)(DateTimeOffset.UtcNow - process.StartTime).TotalSeconds,
            ProcessMemoryBytes = process.WorkingSet64,
            GcHeapBytes = GC.GetTotalMemory(false),
            ThreadCount = process.Threads.Count,
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Buffer = _buffer.GetStats(),
            DuckDbMemoryLimit = _duckDb.EffectiveMemoryLimit,
        };

        var today = DateTimeOffset.UtcNow;
        var todayTables = new List<(string Name, string TableName)>();
        foreach (var (prefix, name) in new[]
                 {
                     (TimeTables.LogsPrefix, "logs"),
                     (TimeTables.TracesPrefix, "traces"),
                     (TimeTables.MetricsPrefix, "metrics"),
                     (TimeTables.IotPrefix, "iot"),
                 })
        {
            var tables = _storeInfo.GetTableNames(prefix);
            response.TableCounts[name] = tables.Count;
            todayTables.Add((name, TimeTables.Name(prefix, today)));
        }

        var todayRows = await _storeInfo.CountTablesAsync(todayTables, cancellationToken);
        foreach (var (name, rows) in todayRows)
        {
            response.TodayRows[name] = rows;
        }

        return response;
    }
}
