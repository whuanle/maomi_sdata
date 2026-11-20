using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using Microsoft.Extensions.Logging;
using SData.Infra.Configuration;
using SData.Store;

namespace SData.Ingest.Consumers;

/// <summary>
/// Apache Pulsar 实例消费 Runner（DotPulsar，逐条 Ack，at-least-once），由 MqConsumerManagerService 按实例调度.
/// 每 topic 一个 consumer 共享订阅名；背压：缓冲满时重试、不 Ack，消费中断后消息按订阅重投递.
/// 韧性：断流（服务端断开等）时抛出，由外层延迟重连.
/// </summary>
public static class PulsarInstanceRunner
{
    /// <summary>
    /// 单实例消费主循环（直到取消；异常后延迟重连）.
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
        var invalid = instance.Topics.Count - topics.Count;
        if (invalid > 0)
        {
            logger.LogWarning("Pulsar 实例 {Name} 有 {Count} 个 topic 的解析规则不在白名单（otlp-proto/otlp-json/json/regex/csv/logfmt/syslog/raw），已忽略", instance.Name, invalid);
        }

        logger.LogInformation("Pulsar 实例消费循环启动（实例={Name}，serviceUrl={Url}，subscription={Subscription}，topics={Topics}）", instance.Name, instance.ServiceUrl, instance.SubscriptionName, string.Join(",", topics.Select(t => t.Name)));

        while (!stoppingToken.IsCancellationRequested)
        {
            MqRuntime.ResetPending(instance.Id);
            try
            {
                await ConsumeAsync(instance, topics, buffer, decoder, logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                MqRuntime.SetState(instance.Id, "error", "Pulsar 消费失败，请检查连接与发现配置");
                logger.LogWarning(ex, "Pulsar 消费异常（实例={Name}），按退避策略重试", instance.Name);
                await MqRetry.WaitAsync(instance, stoppingToken);
            }
        }
    }

    /// <summary>
    /// 每 topic 一个 consumer 的接收循环（订阅名共享）；断流时抛出，由外层重连.
    /// </summary>
    private static async Task ConsumeAsync(MqInstanceOptions instance, List<MqTopicOptions> topics, IIngestBuffer buffer, IMqLogDecoder decoder, ILogger logger, CancellationToken stoppingToken)
    {
        var client = PulsarClient.Builder().ServiceUrl(new Uri(instance.ServiceUrl)).Build();
        var consumers = new List<IConsumer<byte[]>>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var token = linked.Token;
        var loops = new Dictionary<string, Task>(StringComparer.Ordinal);
        var connections = new Dictionary<string, (Task Ready, DateTimeOffset Started)>(StringComparer.Ordinal);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var discovered = await MqTopicDiscovery.DiscoverAsync(instance, token);
                foreach (var rule in topics)
                    MqRuntime.Matched(instance.Id, rule.Name, rule.MatchMode, discovered.Where(t => MqTopicMatcher.Match(topics, t.Name) == rule).Select(t => t.Name));
                foreach (var topic in discovered.Where(t => !loops.ContainsKey(t.Name)))
                {
                    var rule = MqTopicMatcher.Match(topics, topic.Name)!;
                    MqRuntime.TopicState(instance.Id, rule.Name, rule.MatchMode, topic.Name, "starting");
                    var hasConnected = false;
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    connections[topic.Name] = (ready.Task, DateTimeOffset.UtcNow);
                    var consumer = client.NewConsumer(Schema.ByteArray)
                        .SubscriptionName(instance.SubscriptionName)
                        .Topic(topic.Name)
                        .SubscriptionType(ToSubscriptionType(instance.SubscriptionType))
                        .InitialPosition(SubscriptionInitialPosition.Earliest)
                        .StateChangedHandler(change =>
                        {
                            if (token.IsCancellationRequested) return;
                            var connected = change.ConsumerState is ConsumerState.Active or ConsumerState.Inactive;
                            if (connected)
                            {
                                hasConnected = true;
                                ready.TrySetResult();
                                MqRuntime.TopicState(instance.Id, rule.Name, rule.MatchMode, topic.Name, "connected");
                                MqRuntime.Connected(instance.Id);
                            }
                            else if (hasConnected || change.ConsumerState is ConsumerState.Faulted or ConsumerState.Closed or ConsumerState.Unsubscribed)
                            {
                                MqRuntime.TopicState(instance.Id, rule.Name, rule.MatchMode, topic.Name, "error");
                                disconnected.TrySetResult();
                            }
                        })
                        .Create();
                    consumers.Add(consumer);
                    loops.Add(topic.Name, ReceiveLoopAsync(instance, consumer, topic, buffer, decoder, logger, token));
                }

                if (connections.Values.Any(connection => !connection.Ready.IsCompleted && DateTimeOffset.UtcNow - connection.Started >= TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Pulsar 主题首次连接超时");
                var refresh = Task.Delay(TimeSpan.FromSeconds(Math.Min(instance.DiscoveryIntervalSeconds, 5)), token);
                var completed = await Task.WhenAny(loops.Values.Append(refresh).Append(disconnected.Task));
                await completed;
                if (completed != refresh)
                {
                    throw new InvalidOperationException("Pulsar 主题接收循环已结束");
                }
            }
        }
        finally
        {
            linked.Cancel();
            try
            {
                await Task.WhenAll(loops.Values);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Pulsar 接收循环清理");
            }

            foreach (var consumer in consumers)
            {
                try
                {
                    await consumer.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Pulsar consumer 释放异常（停机清理，可忽略）");
                }
            }

            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 单个 consumer 的接收循环：接收 → 解码入缓冲 → Ack.
    /// </summary>
    private static async Task ReceiveLoopAsync(MqInstanceOptions instance, IConsumer<byte[]> consumer, MqTopicOptions topic, IIngestBuffer buffer, IMqLogDecoder decoder, ILogger logger, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var message = await consumer.Receive(stoppingToken).ConfigureAwait(false);
            MqRuntime.Connected(instance.Id);
            await ConsumeMessageAsync(instance, topic, message.Value(), buffer, decoder, logger, stoppingToken).ConfigureAwait(false);
            try
            {
                await consumer.Acknowledge(message.MessageId, stoppingToken).ConfigureAwait(false);
                MqConsumerSupport.Confirmed(instance, topic);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                MqConsumerSupport.ConfirmationFailed(instance, topic);
                throw;
            }
        }
    }

    /// <summary>
    /// 解码并入缓冲（Ack 由调用方在成功后执行）.
    /// </summary>
    private static async Task ConsumeMessageAsync(MqInstanceOptions instance, MqTopicOptions topic, byte[]? payload, IIngestBuffer buffer, IMqLogDecoder decoder, ILogger logger, CancellationToken stoppingToken)
    {
        await MqConsumerSupport.ProcessMessageAsync(instance, topic, payload ?? [], $"pulsar:{topic.Name}",
            buffer, decoder, logger, stoppingToken);
    }

    private static SubscriptionType ToSubscriptionType(string value)
    {
        return value?.ToLowerInvariant().Replace("-", "_") switch
        {
            "failover" => SubscriptionType.Failover,
            "exclusive" => SubscriptionType.Exclusive,
            "key_shared" or "keyshared" => SubscriptionType.KeyShared,
            _ => SubscriptionType.Shared,
        };
    }

    private static async Task SafeDelayAsync(int seconds, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 停机取消，直接返回
        }
    }
}
