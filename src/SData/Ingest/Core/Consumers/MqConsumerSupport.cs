using SData.Infra.Configuration;
using SData.Store;
using SData.Store.Models;

namespace SData.Ingest.Consumers;

/// <summary>
/// MQ 消费者公共常量与工具.
/// </summary>
public static class MqConsumerSupport
{
    /// <summary>原消息必须完整解析并提交后才返回；失败阻塞并退避，不确认也不跳过.</summary>
    public static async Task ProcessMessageAsync(MqInstanceOptions instance, MqTopicOptions topic, byte[] payload, string source,
        IIngestBuffer buffer, IMqLogDecoder decoder, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
    {
        Received(instance, topic, payload.Length);
        var rule = MqTopicMatcher.Match(instance.Topics, topic.Name) ?? topic;
        var token = Guid.NewGuid();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<LogRecord> records;
                string? error;
                bool decoded;
                try { decoded = decoder.TryDecode(payload, topic, source, out records, out error); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    decoded = false;
                    records = [];
                    error = "解析器异常";
                }
                if (!decoded || error != null || records.Count == 0
                    || records.Any(record => MqLogDecoder.GetParseError(record) != null))
                {
                    MqRuntime.Blocked(instance.Id, rule.Name, rule.MatchMode, token);
                    Decoded(instance, topic, true, error);
                    Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger,
                        "MQ 消息解析失败，保持未确认并退避重试（实例={Instance}，topic={Topic}，格式={Format}）",
                        instance.Name, topic.Name, topic.Format);
                    await MqRetry.WaitAsync(instance, cancellationToken);
                    continue;
                }

                Decoded(instance, topic, false);
                try
                {
                    await PersistAsync(instance, topic, records, buffer, cancellationToken);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    MqRuntime.Blocked(instance.Id, rule.Name, rule.MatchMode, token);
                    Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, exception,
                        "MQ 消息持久化失败，保持未确认并退避重试（实例={Instance}，topic={Topic}）", instance.Name, topic.Name);
                    await MqRetry.WaitAsync(instance, cancellationToken);
                }
            }
        }
        finally { MqRuntime.Unblocked(instance.Id, rule.Name, rule.MatchMode, token); }
    }

    /// <summary>附加受信任来源属性并等待日志持久化.</summary>
    public static async Task PersistAsync(MqInstanceOptions instance, MqTopicOptions topic, List<LogRecord> records, IIngestBuffer buffer, CancellationToken cancellationToken)
    {
        if (records.Count == 0 || records.Any(record => MqLogDecoder.GetParseError(record) != null))
            throw new InvalidOperationException("消息未完整解析，禁止持久化后确认");
        var rule = MqTopicMatcher.Match(instance.Topics, topic.Name);
        foreach (var record in records)
        {
            var attributes = string.IsNullOrEmpty(record.AttributesJson)
                ? new System.Text.Json.Nodes.JsonObject()
                : System.Text.Json.Nodes.JsonNode.Parse(record.AttributesJson) as System.Text.Json.Nodes.JsonObject ?? new();
            attributes["_mq_instance"] = instance.Name;
            attributes["_mq_rule"] = rule?.Name ?? topic.Name;
            attributes["_mq_topic"] = topic.Name;
            record.AttributesJson = attributes.ToJsonString();
        }

        var name = rule?.Name ?? topic.Name;
        var mode = rule?.MatchMode ?? topic.MatchMode;
        var persistenceToken = MqRuntime.BeginPersistence(instance.Id, name, mode);
        try
        {
            await buffer.PersistLogsAsync(records, cancellationToken);
            MqRuntime.Persisted(instance.Id, name, mode, records.Count);
            MqRuntime.ConfirmationPending(instance.Id, name, mode, 1);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            MqRuntime.PersistenceFailed(instance.Id, name, mode);
            MqFailureEvents.Publish(new(instance.Id, topic.Name, "persist", "persistence_failed", "日志持久化失败，消息尚未确认", DateTimeOffset.UtcNow));
            throw;
        }
        finally
        {
            MqRuntime.EndPersistence(instance.Id, name, mode, persistenceToken);
        }
    }

    /// <summary>按配置规则记录接收，不展开实际队列.</summary>
    public static void Received(MqInstanceOptions instance, MqTopicOptions topic, int bytes)
    {
        var rule = MqTopicMatcher.Match(instance.Topics, topic.Name) ?? topic;
        MqRuntime.Received(instance.Id, rule.Name, rule.MatchMode, topic.Name, bytes);
    }

    public static string? Decoded(MqInstanceOptions instance, MqTopicOptions topic, bool failed, string? error = null)
    {
        var rule = MqTopicMatcher.Match(instance.Topics, topic.Name) ?? topic;
        MqRuntime.Decoded(instance.Id, rule.Name, rule.MatchMode, failed);
        if (failed)
        {
            var code = topic.Format == "regex" ? "regex_unmatched" : topic.Format is "json" or "otlp-json" ? "json_invalid" : "payload_invalid";
            var reason = code == "regex_unmatched" ? "正则未匹配消息，请检查规则和日志格式"
                : code == "json_invalid" ? "JSON 不完整、形状不符或字段类型错误，请检查生产端和字段映射" : "载荷与解析格式不匹配";
            if (code == "json_invalid" && error != null)
            {
                var position = System.Text.RegularExpressions.Regex.Match(error, @"LineNumber:\s*(\d+)\s*\|\s*BytePositionInLine:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));
                if (position.Success) reason += $"（行 {position.Groups[1].Value}，字节位置 {position.Groups[2].Value}）";
            }

            MqFailureEvents.Publish(new(instance.Id, topic.Name, "parse", code, reason, DateTimeOffset.UtcNow));
            return MqRuntime.ParseFailed(instance.Id, rule.Name, rule.MatchMode, code, reason, topic.Name);
        }

        return null;
    }

    public static void Confirmed(MqInstanceOptions instance, MqTopicOptions topic, int count = 1)
    {
        var rule = MqTopicMatcher.Match(instance.Topics, topic.Name) ?? topic;
        MqRuntime.Confirmed(instance.Id, rule.Name, rule.MatchMode, count);
        MqRetry.Progress(instance.Id);
    }

    public static void ConfirmationFailed(MqInstanceOptions instance, MqTopicOptions topic)
    {
        var rule = MqTopicMatcher.Match(instance.Topics, topic.Name) ?? topic;
        MqRuntime.ConfirmationFailed(instance.Id, rule.Name, rule.MatchMode);
        MqFailureEvents.Publish(new(instance.Id, topic.Name, "confirm", "confirmation_failed", "消息确认失败，可能重投递", DateTimeOffset.UtcNow));
    }

    /// <summary>缓冲满时的重试间隔（毫秒）.</summary>
    public const int RetryDelayMs = 500;

    /// <summary>broker 连接异常后的重连间隔（秒）.</summary>
    public const int ReconnectDelaySeconds = 5;

    /// <summary>
    /// 把一批日志记录全部写入缓冲；缓冲满时退避重试直到全部入队或取消（不丢数据，at-least-once）.
    /// 取消令牌触发时抛 OperationCanceledException，由调用方保持消息未确认.
    /// </summary>
    /// <param name="buffer">接入缓冲.</param>
    /// <param name="records">日志记录.</param>
    /// <param name="onBackpressure">缓冲满首次发生时的回调（用于记一次 warning，可空）.</param>
    /// <param name="delay">重试间隔（测试注入用；缺省 RetryDelayMs）.</param>
    /// <param name="cancellationToken">取消令牌.</param>
    /// <returns>异步任务.</returns>
    public static async Task EnqueueAllAsync(
        IIngestBuffer buffer,
        List<SData.Store.Models.LogRecord> records,
        Action? onBackpressure = null,
        int delay = RetryDelayMs,
        CancellationToken cancellationToken = default)
    {
        var backpressureLogged = false;
        var batch = buffer.TryEnqueueLogs(records);
        if (batch.Dropped == 0)
        {
            return;
        }

        if (!backpressureLogged)
        {
            backpressureLogged = true;
            onBackpressure?.Invoke();
        }

        foreach (var record in batch.Rejected)
        {
            while (!buffer.TryEnqueueLog(record))
            {
                if (!backpressureLogged)
                {
                    backpressureLogged = true;
                    onBackpressure?.Invoke();
                }

                await Task.Delay(delay, cancellationToken);
            }
        }
    }
}
