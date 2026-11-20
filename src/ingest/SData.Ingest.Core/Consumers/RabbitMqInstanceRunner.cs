using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SData.Infra.Configuration;
using SData.Store;

namespace SData.Ingest.Consumers;

/// <summary>
/// RabbitMQ 实例消费 Runner（RabbitMQ.Client 7.x，手动 Ack，at-least-once），由 MqConsumerManagerService 按实例调度.
/// 仅消费已有队列；背压：缓冲满时等待批量落库、不 Ack，连接断开后消息重投递.
/// 韧性：断线由实例循环退避重连，失败消息保持未确认.
/// </summary>
public static class RabbitMqInstanceRunner
{
    /// <summary>未确认消息预取上限（缓冲满时消费暂停的背压窗口）.</summary>
    private const ushort PrefetchCount = 64;

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
        var queues = MqConsumerManagerService.ValidTopics(instance);
        var invalid = instance.Topics.Count - queues.Count;
        if (invalid > 0)
        {
            logger.LogWarning("RabbitMQ 实例 {Name} 有 {Count} 个队列的解析规则不在白名单（otlp-proto/otlp-json/json/regex/csv/logfmt/syslog/raw），已忽略", instance.Name, invalid);
        }

        logger.LogInformation("RabbitMQ 实例消费循环启动（实例={Name}，queues={Queues}）", instance.Name, string.Join(",", queues.Select(q => q.Name)));

        while (!stoppingToken.IsCancellationRequested)
        {
            MqRuntime.ResetPending(instance.Id);
            try
            {
                await ConnectAndConsumeAsync(instance, queues, buffer, decoder, logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                MqRuntime.SetState(instance.Id, "error", "RabbitMQ 消费失败，请检查连接与管理权限");
                logger.LogWarning(ex, "RabbitMQ 消费异常（实例={Name}），按退避策略重试", instance.Name);
                await MqRetry.WaitAsync(instance, stoppingToken);
            }
        }
    }

    /// <summary>
    /// 建立连接并消费全部队列；断线取消旧消息等待，由外层退避重连.
    /// </summary>
    private static async Task ConnectAndConsumeAsync(MqInstanceOptions instance, List<MqTopicOptions> queues, IIngestBuffer buffer, IMqLogDecoder decoder, ILogger logger, CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(instance.Uri),
            AutomaticRecoveryEnabled = false,
        };
        using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.BasicQosAsync(0, PrefetchCount, false, stoppingToken);
        logger.LogInformation("RabbitMQ 连接已建立（实例={Name}），开始消费 {Count} 个队列", instance.Name, queues.Count);

        MqRuntime.Connected(instance.Id);
        connection.ConnectionShutdownAsync += (_, _) =>
        {
            connectionStop.Cancel();
            if (!stoppingToken.IsCancellationRequested)
                MqRuntime.SetState(instance.Id, "error", "RabbitMQ 连接中断，等待恢复");
            return Task.CompletedTask;
        };
        channel.ChannelShutdownAsync += (_, _) =>
        {
            connectionStop.Cancel();
            return Task.CompletedTask;
        };
        var subscribed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!connection.IsOpen || !channel.IsOpen) throw new IOException("RabbitMQ 连接或通道已关闭");
                var discovered = await MqTopicDiscovery.DiscoverAsync(instance, stoppingToken);
                foreach (var rule in queues)
                    MqRuntime.Matched(instance.Id, rule.Name, rule.MatchMode, discovered.Where(q => MqTopicMatcher.Match(queues, q.Name) == rule).Select(q => q.Name));
                foreach (var queue in discovered.Where(q => !subscribed.Contains(q.Name)))
                {
                    await channel.QueueDeclarePassiveAsync(queue.Name, stoppingToken);
                    var consumer = new AsyncEventingBasicConsumer(channel);
                    consumer.ReceivedAsync += (_, ea) => OnMessageAsync(instance, channel, queue, ea, buffer, decoder, logger, connectionStop.Token);
                    await channel.BasicConsumeAsync(queue.Name, autoAck: false, consumer, cancellationToken: stoppingToken);
                    subscribed.Add(queue.Name);
                }

                await Task.Delay(TimeSpan.FromSeconds(instance.DiscoveryIntervalSeconds), stoppingToken);
            }
        }
        finally { connectionStop.Cancel(); }
    }

    /// <summary>
    /// 完整解析并提交后才 Ack；失败保持原消息并退避重试.
    /// </summary>
    private static async Task OnMessageAsync(MqInstanceOptions instance, IChannel channel, MqTopicOptions queue, BasicDeliverEventArgs ea, IIngestBuffer buffer, IMqLogDecoder decoder, ILogger logger, CancellationToken stoppingToken)
    {
        var payload = ea.Body.ToArray();
        await MqConsumerSupport.ProcessMessageAsync(instance, queue, payload, $"rabbitmq:{queue.Name}", buffer, decoder, logger, stoppingToken);
        while (true)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, stoppingToken);
                MqConsumerSupport.Confirmed(instance, queue);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                MqConsumerSupport.ConfirmationFailed(instance, queue);
                logger.LogWarning(exception, "RabbitMQ 确认失败，保持原消息未确认并退避重试（queue={Queue}）", queue.Name);
                await MqRetry.WaitAsync(instance, stoppingToken);
            }
        }
    }

}
