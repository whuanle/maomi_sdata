using Mediator;
using SData.Store.Models;

namespace SData.Monitor;

/// <summary>
/// 服务状态.
/// </summary>
public class StatusResponse
{
    /// <summary>消息队列消费运行态（进程内累计，重启归零）.</summary>
    public IReadOnlyList<SData.Ingest.MqRuntimeItem> MqConsumers { get; set; } = [];

    /// <summary>后台采样的系统与进程资源.</summary>
    public ResourceSnapshot Resources { get; set; } = new();

    /// <summary>服务名.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>版本.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>运行时长（秒）.</summary>
    public long UptimeSeconds { get; set; }

    /// <summary>进程内存（字节）.</summary>
    public long ProcessMemoryBytes { get; set; }

    /// <summary>GC 堆内存（字节）.</summary>
    public long GcHeapBytes { get; set; }

    /// <summary>线程数.</summary>
    public int ThreadCount { get; set; }

    /// <summary>操作系统.</summary>
    public string Os { get; set; } = string.Empty;

    /// <summary>接入缓冲统计.</summary>
    public Store.Models.IngestBufferStats? Buffer { get; set; }

    /// <summary>各域表数量.</summary>
    public Dictionary<string, int> TableCounts { get; set; } = new();

    /// <summary>今日各域行数.</summary>
    public Dictionary<string, long> TodayRows { get; set; } = new();

    /// <summary>DuckDB 内存上限.</summary>
    public string DuckDbMemoryLimit { get; set; } = string.Empty;
}

/// <summary>
/// 服务状态查询.
/// </summary>
public class QueryStatusCommand : IRequest<StatusResponse>
{
}
