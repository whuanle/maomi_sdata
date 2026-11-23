using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SData.Infra.Services;

namespace SData.Store.Core;

/// <summary>
/// resource JSON 字典编码器（logs/traces 写入去重的核心）：
/// 同一 resource 字符串一个天表周期只落一行 (id, resource) 到字典表，数据行仅存 resource_id。
/// OTLP 解析侧同一个 resource 块共享字符串引用（OtlpLogParser/OtlpTraceParser），写入端以
/// 引用相等做零哈希快路径，未命中再走字符串字典；雪花 id 全局唯一，跨天/重启无冲突。
/// 字典缓存按天分桶并轮换（保留最近 4 天），distinct 数超过阈值告警（真实部署 resource
/// 为服务实例级低基数；异常膨胀说明属性放错位置，不会影响正确性，仅削弱去重收益）.
/// </summary>
public sealed class ResourceInterner(IIdProvider idProvider, ILogger<ResourceInterner>? logger = null)
{
    /// <summary>distinct 数告警阈值（超过说明 resource 基数异常）.</summary>
    public const int DistinctWarnThreshold = 10_000;

    /// <summary>保留的天桶数（当前 + 迟到数据覆盖的前几日）.</summary>
    private const int MaxDayBuckets = 4;

    private readonly ConcurrentDictionary<DateTimeOffset, DayBucket> _buckets = new();
    private object? _lastReference;
    private long _lastReferenceId;

    /// <summary> intern 结果：resource_id（null = 原文为 null，数据行存 NULL）.</summary>
    /// <param name="Id">resource_id；null 表示原文为 null.</param>
    /// <param name="IsNew">本次 intern 是否产生了新字典条目（调用方需把 (id, json) 落字典表）.</param>
    /// <param name="Json">字典表要落的原文（IsNew 时有效）.</param>
    public readonly record struct Result(long? Id, bool IsNew, string? Json);

    /// <summary>
    /// intern 一条 resource 原文.
    /// </summary>
    /// <param name="day">数据天（决定字典桶）.</param>
    /// <param name="resourceJson">resource 原文（null = 无 resource）.</param>
    /// <returns>intern 结果.</returns>
    public Result Intern(DateTimeOffset day, string? resourceJson)
    {
        if (resourceJson == null)
        {
            return new(null, false, null);
        }

        // 引用相等快路径：OTLP 同 resource 块共享同一字符串实例，零哈希命中
        var last = _lastReference;
        if (ReferenceEquals(last, resourceJson))
        {
            return new(_lastReferenceId, false, null);
        }

        var bucket = _buckets.GetOrAdd(day.Date, static _ => new DayBucket());
        if (_buckets.Count > MaxDayBuckets)
        {
            TrimBuckets();
        }

        if (bucket.Map.TryGetValue(resourceJson, out var id))
        {
            _lastReference = resourceJson;
            _lastReferenceId = id;
            return new(id, false, null);
        }

        id = idProvider.GetLongId();
        bucket.Map[resourceJson] = id;
        var count = bucket.Map.Count;
        if (count == DistinctWarnThreshold)
        {
            logger?.LogWarning("Resource distinct count reached {Count} for {Day:yyyyMMdd} — dedup benefit collapsing, check producer resource attributes", count, day);
        }

        _lastReference = resourceJson;
        _lastReferenceId = id;
        return new(id, true, resourceJson);
    }

    /// <summary>丢弃最旧的天桶（桶数超上限时），防止长期运行内存缓慢增长.</summary>
    private void TrimBuckets()
    {
        while (_buckets.Count > MaxDayBuckets)
        {
            var oldest = _buckets.Keys.Min();
            if (!_buckets.TryRemove(oldest, out _))
            {
                break;
            }
        }
    }

    private sealed class DayBucket
    {
        public ConcurrentDictionary<string, long> Map { get; } = new(StringComparer.Ordinal);
    }
}
