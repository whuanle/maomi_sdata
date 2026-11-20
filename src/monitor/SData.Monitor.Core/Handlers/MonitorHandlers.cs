using System.Diagnostics;
using MediatR;
using SData.Infra.Configuration;
using SData.Store;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryStatusCommandHandler"/> class.
    /// </summary>
    /// <param name="buffer">接入缓冲.</param>
    /// <param name="storeInfo">存储信息.</param>
    /// <param name="systemOptions">系统配置.</param>
    public QueryStatusCommandHandler(IIngestBuffer buffer, IStoreInfo storeInfo, SystemOptions systemOptions, ResourceSampler resources)
    {
        _buffer = buffer;
        _storeInfo = storeInfo;
        _systemOptions = systemOptions;
        _resources = resources;
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
            DuckDbMemoryLimit = _systemOptions.DuckDb.MemoryLimit,
        };

        var today = DateTimeOffset.UtcNow;
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

            var todayTable = TimeTables.Name(prefix, today);
            response.TodayRows[name] = tables.Contains(todayTable)
                ? await _storeInfo.CountTableAsync(todayTable, cancellationToken)
                : 0;
        }

        return response;
    }
}
