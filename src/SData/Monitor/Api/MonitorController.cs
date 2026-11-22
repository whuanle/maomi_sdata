using System.Globalization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SData.Monitor.Api;

/// <summary>
/// 监控接口.
/// </summary>
[ApiController]
[Route("monitor")]
public class MonitorController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly SData.Store.IIngestBuffer _buffer;
    private readonly SData.Store.IStorageMetrics _storageMetrics;
    private readonly SData.Store.IStorePerformanceMetrics _storePerformanceMetrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonitorController"/> class.
    /// </summary>
    /// <param name="mediator">mediator.</param>
    /// <param name="buffer">接入缓冲.</param>
    /// <param name="storageMetrics">后台存储采样.</param>
    /// <param name="storePerformanceMetrics">Store 性能计数快照.</param>
    public MonitorController(
        IMediator mediator,
        SData.Store.IIngestBuffer buffer,
        SData.Store.IStorageMetrics storageMetrics,
        SData.Store.IStorePerformanceMetrics storePerformanceMetrics)
    {
        _mediator = mediator;
        _buffer = buffer;
        _storageMetrics = storageMetrics;
        _storePerformanceMetrics = storePerformanceMetrics;
    }

    /// <summary>
    /// 存活探针.
    /// </summary>
    /// <returns>ok.</returns>
    [HttpGet("/health")]
    [HttpGet("/api/health")]
    [AllowAnonymous]
    public IActionResult Health()
    {
        return Ok(new { status = "ok" });
    }

    /// <summary>
    /// Prometheus 指标文本.
    /// </summary>
    /// <returns>文本.</returns>
    [HttpGet("/metrics")]
    [AllowAnonymous]
    [Produces("text/plain")]
    public async Task<string> Metrics(CancellationToken cancellationToken)
    {
        var status = await _mediator.Send(new QueryStatusCommand(), cancellationToken);
        var lines = new List<string>
        {
            "# HELP sdata_uptime_seconds Service uptime in seconds.",
            "# TYPE sdata_uptime_seconds gauge",
            $"sdata_uptime_seconds {status.UptimeSeconds}",
            "# HELP sdata_process_memory_bytes Process working set bytes.",
            "# TYPE sdata_process_memory_bytes gauge",
            $"sdata_process_memory_bytes {status.ProcessMemoryBytes}",
            "# HELP sdata_gc_heap_bytes GC heap bytes.",
            "# TYPE sdata_gc_heap_bytes gauge",
            $"sdata_gc_heap_bytes {status.GcHeapBytes}",
            "# HELP sdata_gc_committed_bytes GC committed memory in bytes.",
            "# TYPE sdata_gc_committed_bytes gauge",
            $"sdata_gc_committed_bytes {GC.GetGCMemoryInfo().TotalCommittedBytes}",
            "# HELP sdata_gc_pause_seconds_total Total time paused for garbage collection in seconds.",
            "# TYPE sdata_gc_pause_seconds_total counter",
            $"sdata_gc_pause_seconds_total {GC.GetTotalPauseDuration().TotalSeconds.ToString("R", CultureInfo.InvariantCulture)}",
            "# HELP sdata_gc_collection_count_total GC collection count since process start.",
            "# TYPE sdata_gc_collection_count_total counter",
            $"sdata_gc_collection_count_total{{generation=\"0\"}} {status.Resources.GcCollectionCount0}",
            $"sdata_gc_collection_count_total{{generation=\"1\"}} {status.Resources.GcCollectionCount1}",
            $"sdata_gc_collection_count_total{{generation=\"2\"}} {status.Resources.GcCollectionCount2}",
            "# HELP sdata_threads Threads count.",
            "# TYPE sdata_threads gauge",
            $"sdata_threads {status.ThreadCount}",
        };

        var storage = _storageMetrics.Local;
        if (storage.State is "ok" or "partial" && storage.DuckDbState == "ok" && storage.TrackedMemoryBytes is long trackedBytes)
        {
            lines.Add("# HELP sdata_duckdb_tracked_memory_bytes DuckDB tracked memory from the latest successful sample.");
            lines.Add("# TYPE sdata_duckdb_tracked_memory_bytes gauge");
            lines.Add($"sdata_duckdb_tracked_memory_bytes {trackedBytes}");
        }

        if (storage.State is "ok" or "partial" && storage.FileState == "ok" && storage.WalBytes is long walBytes)
        {
            lines.Add("# HELP sdata_duckdb_wal_bytes DuckDB WAL size from the latest successful sample.");
            lines.Add("# TYPE sdata_duckdb_wal_bytes gauge");
            lines.Add($"sdata_duckdb_wal_bytes {walBytes}");
        }

        var storePerformance = _storePerformanceMetrics.Snapshot;
        foreach (var histogram in storePerformance.Histograms)
        {
            lines.Add($"# HELP {histogram.Name} Cumulative duration histogram in seconds.");
            lines.Add($"# TYPE {histogram.Name} histogram");
            for (var index = 0; index < histogram.UpperBoundsSeconds.Count; index++)
            {
                var bound = double.IsPositiveInfinity(histogram.UpperBoundsSeconds[index])
                    ? "+Inf"
                    : histogram.UpperBoundsSeconds[index].ToString("R", CultureInfo.InvariantCulture);
                lines.Add($"{histogram.Name}_bucket{{le=\"{bound}\"}} {histogram.BucketCounts[index]}");
            }

            lines.Add($"{histogram.Name}_count {histogram.Count}");
            lines.Add($"{histogram.Name}_sum {histogram.SumSeconds.ToString("R", CultureInfo.InvariantCulture)}");
        }

        foreach (var counter in storePerformance.Counters)
        {
            lines.Add($"# HELP {counter.Name} Cumulative Store row or work-item count.");
            lines.Add($"# TYPE {counter.Name} counter");
            lines.Add($"{counter.Name} {counter.Value}");
        }

        if (status.Buffer != null)
        {
            foreach (var kind in status.Buffer.Buffered.Keys)
            {
                lines.Add($"sdata_buffer_count{{kind=\"{kind}\"}} {status.Buffer.Buffered[kind]}");
                lines.Add($"sdata_ingest_processed_total{{kind=\"{kind}\"}} {status.Buffer.Processed.GetValueOrDefault(kind, 0)}");
                lines.Add($"sdata_ingest_dropped_total{{kind=\"{kind}\"}} {status.Buffer.Dropped.GetValueOrDefault(kind, 0)}");
            }
        }

        foreach (var (name, count) in status.TodayRows)
        {
            lines.Add($"sdata_today_rows{{domain=\"{name}\"}} {count}");
        }

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// 服务状态详情.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>状态.</returns>
    [HttpGet("status")]
    [Authorize]
    public async Task<StatusResponse> Status(CancellationToken cancellationToken)
    {
        return await _mediator.Send(new QueryStatusCommand(), cancellationToken);
    }

    /// <summary>消费状态与历史采样，不返回连接配置.</summary>
    [HttpGet("mq-consumption")]
    [Authorize(Roles = "admin")]
    public Task<QueryMqConsumptionResponse> MqConsumption([FromQuery] QueryMqConsumptionCommand command, CancellationToken cancellationToken)
        => _mediator.Send(command, cancellationToken);

    /// <summary>立即重试整个消费实例.</summary>
    [HttpPost("mq-consumption/retry")]
    [Authorize(Roles = "admin")]
    public Task<bool> RetryMq([FromBody] RetryMqConsumerCommand command, CancellationToken cancellationToken)
        => _mediator.Send(command, cancellationToken);

    /// <summary>
    /// 总览页聚合：通道健康 / 今日量 / 存储用量.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>总览聚合.</returns>
    [HttpGet("/api/overview/status")]
    [Authorize]
    public async Task<OverviewResponse> Overview(CancellationToken cancellationToken)
    {
        return await _mediator.Send(new QueryOverviewCommand(), cancellationToken);
    }
}
