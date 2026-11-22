using Mediator;
using SData.Infra.Services;

namespace SData.Monitor;

/// <summary>
/// 总览页聚合查询：通道健康/今日量/存储用量.
/// </summary>
public class QueryOverviewCommand : IRequest<OverviewResponse>, IModelValidator<QueryOverviewCommand>
{
    /// <inheritdoc/>
    public static void Validate(FluentValidation.AbstractValidator<QueryOverviewCommand> validator)
    {
        // 无入参，无需校验
    }
}

/// <summary>
/// 总览页聚合响应.
/// </summary>
public class OverviewResponse
{
    /// <summary>今日各通道（按数据类型）写入行数.</summary>
    public Dictionary<string, long> TodayRows { get; set; } = new();

    /// <summary>各通道（按数据类型）天表数量.</summary>
    public Dictionary<string, int> TableCounts { get; set; } = new();

    /// <summary>接入通道健康表（按数据类型聚合缓冲统计）.</summary>
    public List<OverviewChannel> Channels { get; set; } = new();

    /// <summary>存储用量.</summary>
    public OverviewStorage Storage { get; set; } = new();

    public SData.Store.LocalStorageSnapshot LocalResources { get; set; } = new();

    public SData.Store.S3StorageSnapshot S3Resources { get; set; } = new();

    /// <summary>共享摄取队列长度（不能按通道累加）.</summary>
    public long BufferedRecords { get; set; }

    /// <summary>配置实例与进程运行态合并后的安全摘要.</summary>
    public List<OverviewMqConsumer> MqConsumers { get; set; } = new();
}

/// <summary>MQ 日志消费安全摘要，不含连接参数与原始异常.</summary>
public class OverviewMqConsumer
{
    public long InstanceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public string State { get; set; } = "unknown";

    public long PersistedRecords { get; set; }

    public DateTimeOffset? LastPersistedAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// 接入通道健康项.
/// </summary>
public class OverviewChannel
{
    /// <summary>通道名（数据类型：log/trace/metric/telemetry/rumevent/rumsession/rumreplay）.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>已落库条数.</summary>
    public long Processed { get; set; }

    /// <summary>因缓冲满丢弃条数.</summary>
    public long Dropped { get; set; }

    /// <summary>缓冲中条数.</summary>
    public long Buffered { get; set; }

    /// <summary>健康（无丢弃即健康）.</summary>
    public bool Healthy => Dropped == 0;
}

/// <summary>
/// 存储用量.
/// </summary>
public class OverviewStorage
{
    /// <summary>热层 DuckDB 文件大小（字节）；文件未创建时为 0.</summary>
    public long HotDbBytes { get; set; }

    /// <summary>热层数据库文件路径（相对展示）.</summary>
    public string HotDbPath { get; set; } = string.Empty;

    /// <summary>S3 目标是否已配置；不代表连通性已验证或本地冷存是否启用.</summary>
    public bool ColdEnabled { get; set; }

    /// <summary>DuckDB 内存上限（配置原样）.</summary>
    public string MemoryLimit { get; set; } = string.Empty;
}
