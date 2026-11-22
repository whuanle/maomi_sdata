using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using SData.Infra.Configuration;
using SData.Store;

namespace SData.Ingest.Consumers;

/// <summary>
/// Kafka 实例消费 Runner（Confluent.Kafka，手动提交 offset，at-least-once），由 MqConsumerManagerService 按实例调度.
/// 背压：缓冲满时暂停拉取重试、不提交 offset，重启/再平衡后消息重投递；解析失败保持原消息阻塞重试，不推进消费位置.
/// 韧性：broker 不可达时记 warning 延迟重连，不影响宿主.
/// </summary>
public static class KafkaInstanceRunner
{
    /// <summary>单批最多提交消息数（达到即提交 offset）.</summary>
    private const int CommitBatchMessages = 200;

    /// <summary>单批最长等待时间（毫秒，凑不满一批也提交）.</summary>
    private const int CommitIntervalMs = 1_000;

    /// <summary>单次 Consume 轮询超时（毫秒）.</summary>
    private const int PollTimeoutMs = 500;

    /// <summary>
    /// 单实例消费主循环（直到取消；连接异常后延迟重连）.
    /// </summary>
    /// <param name="instance">实例配置.</param>
    /// <param name="buffer">接入缓冲.</param>
    /// <param name="decoder">消息解码.</param>
    /// <param name="logger">日志.</param>
    /// <param name="stoppingToken">取消令牌（管理器热更新/停机触发）.</param>
    /// <returns>异步任务.</returns>
    public static async Task ConsumeAsync(MqInstanceOptions instance, IIngestBuffer buffer, IMqLogDecoder decoder, ILogger logger, CancellationToken stoppingToken)
    {
        var topics = MqConsumerManagerService.ValidTopics(instance);
        var settings = topics;
        var invalid = instance.Topics.Count - topics.Count;
        if (invalid > 0)
        {
            logger.LogWarning("Kafka 实例 {Name} 有 {Count} 个 topic 的解析规则不在白名单（otlp-proto/otlp-json/json/regex/csv/logfmt/syslog/raw），已忽略", instance.Name, invalid);
        }

        logger.LogInformation("Kafka 实例消费循环启动（实例={Name}，brokers={Brokers}，group={Group}，topics={Topics}）", instance.Name, instance.BootstrapServers, instance.GroupId, string.Join(",", settings.Select(MqTopicMatcher.KafkaSubscription)));

        while (!stoppingToken.IsCancellationRequested)
        {
            MqRuntime.ResetPending(instance.Id);
            try
            {
                await ConsumeLoopAsync(instance, settings, buffer, decoder, logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                MqRuntime.SetState(instance.Id, "error", "Kafka 消费失败，请检查连接与认证");
                logger.LogWarning(ex, "Kafka 消费异常（实例={Name}），按退避策略重试", instance.Name);
                await MqRetry.WaitAsync(instance, stoppingToken);
            }
        }
    }

    /// <summary>
    /// 连接并消费：一批内连续拉取，凑批后统一提交，降低 CommitSync 次数.
    /// </summary>
    private static async Task ConsumeLoopAsync(MqInstanceOptions instance, List<MqTopicOptions> settings, IIngestBuffer buffer, IMqLogDecoder decoder, ILogger logger, CancellationToken stoppingToken)
    {
        var pendingOffsets = new Dictionary<TopicPartition, (TopicPartitionOffset Offset, string Topic, int Count)>();
        var connectionFailed = 0;
        using var consumer = new ConsumerBuilder<byte[]?, byte[]?>(new ConsumerConfig
        {
            BootstrapServers = instance.BootstrapServers,
            GroupId = instance.GroupId,
            AutoOffsetReset = string.Equals(instance.AutoOffsetReset, "latest", StringComparison.OrdinalIgnoreCase)
                ? AutoOffsetReset.Latest
                : AutoOffsetReset.Earliest,
            MetadataMaxAgeMs = Math.Clamp(instance.DiscoveryIntervalSeconds, 5, 300) * 1000,
            TopicMetadataRefreshIntervalMs = Math.Clamp(instance.DiscoveryIntervalSeconds, 5, 300) * 1000,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            SecurityProtocol = Enum.Parse<SecurityProtocol>(instance.SecurityProtocol),
            SaslMechanism = Enum.Parse<SaslMechanism>(instance.SaslMechanism),
            SaslUsername = instance.SaslUsername,
            SaslPassword = instance.SaslPassword,
        })
            .SetPartitionsAssignedHandler((_, partitions) =>
            {
                MqRuntime.Connected(instance.Id);
                foreach (var rule in settings)
                    MqRuntime.Matched(instance.Id, rule.Name, rule.MatchMode, partitions.Select(p => p.Topic).Distinct().Where(name => MqTopicMatcher.Match(settings, name) == rule));
            })
            .SetPartitionsRevokedHandler((_, partitions) =>
            {
                foreach (var partition in partitions)
                {
                    if (pendingOffsets.Remove(partition.TopicPartition, out var pending))
                    {
                        var rule = MqTopicMatcher.Match(settings, pending.Topic)!;
                        MqRuntime.ConfirmationPending(instance.Id, rule.Name, rule.MatchMode, -pending.Count);
                    }
                }
                MqRuntime.SetState(instance.Id, "starting");
            })
            .SetErrorHandler((_, error) =>
            {
                if (error.IsFatal || error.Code == ErrorCode.Local_AllBrokersDown)
                {
                    Interlocked.Exchange(ref connectionFailed, 1);
                    MqRuntime.SetState(instance.Id, "error", "Kafka 连接异常，等待恢复");
                }
            })
            .Build();
        consumer.Subscribe(settings.Select(MqTopicMatcher.KafkaSubscription));
        logger.LogInformation("Kafka 消费者已连接订阅（实例={Name}，topics={Topics}）", instance.Name, string.Join(",", settings.Select(MqTopicMatcher.KafkaSubscription)));

        while (!stoppingToken.IsCancellationRequested)
        {
            var batchStart = DateTimeOffset.UtcNow;
            var committed = 0;
            while (committed < CommitBatchMessages && DateTimeOffset.UtcNow - batchStart < TimeSpan.FromMilliseconds(CommitIntervalMs) && !stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<byte[]?, byte[]?>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromMilliseconds(PollTimeoutMs));
                }
                catch (ConsumeException ex)
                {
                    logger.LogWarning(ex, "Kafka 拉取异常（实例={Name}，{Reason}），跳过本次轮询", instance.Name, ex.Error.Reason);
                    MqRuntime.SetState(instance.Id, "error", "Kafka 拉取失败，等待重试");
                    await MqRetry.WaitAsync(instance, stoppingToken);
                    break;
                }

                if (Volatile.Read(ref connectionFailed) != 0) throw new IOException("Kafka broker 连接不可用");
                if (result != null) MqRuntime.Connected(instance.Id);
                if (result == null)
                {
                    break;
                }

                var rule = MqTopicMatcher.Match(settings, result.Topic);
                if (rule == null)
                    throw new InvalidOperationException("收到的主题未匹配消费规则，禁止推进消费位置");

                await ConsumeMessageAsync(instance, MqTopicMatcher.ForTopic(rule, result.Topic), result.Message.Value, buffer, decoder, logger, stoppingToken);
                var count = pendingOffsets.TryGetValue(result.TopicPartition, out var pending) ? pending.Count + 1 : 1;
                pendingOffsets[result.TopicPartition] = (new TopicPartitionOffset(result.TopicPartition, result.Offset + 1), result.Topic, count);
                committed++;
            }

            while (pendingOffsets.Count > 0)
            {
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    consumer.Commit(pendingOffsets.Values.Select(x => x.Offset));
                    foreach (var pending in pendingOffsets.Values)
                    {
                        var rule = MqTopicMatcher.Match(settings, pending.Topic)!;
                        MqConsumerSupport.Confirmed(instance, MqTopicMatcher.ForTopic(rule, pending.Topic), pending.Count);
                    }

                    pendingOffsets.Clear();
                }
                catch (TopicPartitionOffsetException ex)
                {
                    foreach (var result in ex.Results)
                    {
                        if (!pendingOffsets.TryGetValue(result.TopicPartition, out var pending)) continue;
                        var rule = MqTopicMatcher.Match(settings, pending.Topic)!;
                        var topic = MqTopicMatcher.ForTopic(rule, pending.Topic);
                        if (result.Error.IsError) MqConsumerSupport.ConfirmationFailed(instance, topic);
                        else
                        {
                            MqConsumerSupport.Confirmed(instance, topic, pending.Count);
                            pendingOffsets.Remove(result.TopicPartition);
                        }
                    }

                    logger.LogWarning(ex, "Kafka 部分分区提交失败（实例={Name}），已落库消息可能重投递", instance.Name);
                    await MqRetry.WaitAsync(instance, stoppingToken);
                }
                catch (KafkaException ex)
                {
                    foreach (var pending in pendingOffsets.Values)
                    {
                        var rule = MqTopicMatcher.Match(settings, pending.Topic)!;
                        MqConsumerSupport.ConfirmationFailed(instance, MqTopicMatcher.ForTopic(rule, pending.Topic));
                    }

                    logger.LogWarning(ex, "Kafka 提交 offset 失败（实例={Name}，{Reason}），已落库消息可能重投递", instance.Name, ex.Error.Reason);
                    await MqRetry.WaitAsync(instance, stoppingToken);
                }
            }
        }

        try
        {
            consumer.Close();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Kafka consumer.Close 异常（停机清理，可忽略）");
        }
    }

    /// <summary>
    /// 单消息完整解析并持久化；失败阻塞原消息，成功后调用方才可提交 offset.
    /// </summary>
    private static async Task ConsumeMessageAsync(MqInstanceOptions instance, MqTopicOptions topic, byte[]? payload, IIngestBuffer buffer, IMqLogDecoder decoder, ILogger logger, CancellationToken stoppingToken)
    {
        await MqConsumerSupport.ProcessMessageAsync(instance, topic, payload ?? [], $"kafka:{topic.Name}",
            buffer, decoder, logger, stoppingToken);
    }

}
