using Microsoft.Extensions.Hosting;
using SData.Store;
using SData.Store.Models;

namespace SData.Ingest.Consumers;

/// <summary>消费统计采样不等待落库，不影响 broker 确认；满缓冲时合并累计快照.</summary>
public class MqConsumptionSampler(IIngestBuffer buffer) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = new Dictionary<(long Id, string Rule, string Mode), MqConsumptionRecord>();
        DateTimeOffset? reportedWriterFailure = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var snapshot = MqRuntime.Snapshot();
            var writerFailure = buffer.GetStats().WriterFailure;
            if (writerFailure is { RecoveredAt: null } && reportedWriterFailure != writerFailure.At)
            {
                foreach (var instance in snapshot.Where(instance => instance.Topics.Any(topic => topic.PendingPersistence > 0)))
                    MqFailureEvents.Publish(new(instance.InstanceId, "", "persist", writerFailure.Code, writerFailure.Reason, writerFailure.At));
                reportedWriterFailure = writerFailure.At;
            }
            var active = snapshot.SelectMany(instance => instance.Topics.Select(topic => (instance.InstanceId, topic.Name, topic.MatchMode))).ToHashSet();
            foreach (var key in pending.Keys.Where(key => !active.Contains(key)).ToList()) pending.Remove(key);
            foreach (var instance in snapshot)
            {
                foreach (var topic in instance.Topics)
                {
                    var key = (instance.InstanceId, topic.Name, topic.MatchMode);
                    pending.TryGetValue(key, out var previous);
                    pending[key] = new MqConsumptionRecord
                    {
                        Timestamp = DateTimeOffset.UtcNow, InstanceId = instance.InstanceId,
                        SessionId = instance.SessionId, Rule = topic.Name, MatchMode = topic.MatchMode,
                        State = topic.State, ReceivedMessages = topic.ReceivedMessages,
                        ReceivedBytes = topic.ReceivedBytes, PersistedRecords = topic.PersistedRecords,
                        ParseFailures = topic.ParseFailures, SkippedMessages = 0,
                        ConfirmedMessages = topic.ConfirmedMessages, ConfirmationFailures = topic.ConfirmationFailures,
                        SamplingGaps = previous == null ? 0 : previous.SamplingGaps + 1,
                        DiagnosticsJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            topic.Stage, topic.StageSince, topic.PendingPersistence, topic.PendingConfirmation,
                            topic.LastProgressAt, topic.Failures, topic.BlockedMessages, topic.BlockedSince,
                        }),
                    };
                }
            }

            foreach (var (key, record) in pending.ToList())
            {
                if (buffer.TryEnqueueMqConsumption(record)) pending.Remove(key);
            }
        }
    }
}
