using System.Text;
using Xunit;
using SData.Infra.Configuration;
using SData.Ingest.Consumers;
using SData.Ingest.Services;
using SData.Store;
using SData.Store.Models;

namespace SData.Ingest.Tests;

[Collection("mq-runtime")]
public class MqRuntimeTests
{
    [Fact]
    public void WindowExpiresWithoutLosingSessionCounters()
    {
        var id = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        try
        {
            MqRuntime.Start(id, [("logs-", "prefix")]);
            MqRuntime.Connected(id);
            Parallel.For(0, 120, _ => MqRuntime.Received(id, "logs-", "prefix", "logs-a", 10));
            MqRuntime.Received(id, "logs-", "prefix", "logs-b", 10);
            MqRuntime.Persisted(id, "logs-", "prefix", 242);
            MqRuntime.Decoded(id, "logs-", "prefix", true);
            MqRuntime.ConfirmationFailed(id, "logs-", "prefix");
            MqRuntime.Persisted(id, "logs-", "prefix", 1);
            var snapshot = MqRuntime.Snapshot().Single(x => x.InstanceId == id);
            var topic = Assert.Single(snapshot.Topics);
            Assert.Equal(121, topic.ReceivedMessages);
            Assert.Equal(2, topic.MatchedTopics);
            Assert.Equal(243, topic.PersistedRecords);
            Assert.Equal(1, topic.ParseFailures);
            Assert.NotNull(topic.LastError);
            Assert.Equal(121 / 60d, topic.MessagesPerSecond);
            var expired = MqRuntime.Snapshot(DateTimeOffset.UtcNow.AddMinutes(2)).Single(x => x.InstanceId == id);
            Assert.Equal("error", expired.State);
            Assert.Equal(0, expired.Topics[0].MessagesPerSecond);
            Assert.Equal(243, expired.PersistedRecords);
            MqRuntime.Confirmed(id, "logs-", "prefix", 121);
            Assert.Null(MqRuntime.Snapshot().Single(x => x.InstanceId == id).Topics[0].LastError);
            MqRuntime.Start(id, [("logs-", "prefix")]);
            var restarted = MqRuntime.Snapshot().Single(x => x.InstanceId == id);
            Assert.NotEqual(snapshot.SessionId, restarted.SessionId);
            Assert.Equal(0, restarted.PersistedRecords);
        }
        finally
        {
            MqRuntime.Remove(id);
        }
    }

    [Fact]
    public void UnknownJsonPreservesOriginalBodyAndReportsFailure()
    {
        const string json = """{"message":"must not disappear"}""";
        var decoder = new MqLogDecoder(new OtlpProtoParserService());
        Assert.True(decoder.TryDecode(Encoding.UTF8.GetBytes(json),
            new MqTopicOptions { Name = "logs", Format = "json" }, "kafka:logs", out var records, out var error));
        Assert.NotNull(error);
        Assert.Equal(json, Assert.Single(records).Body);
        Assert.Contains("_parse_error", records[0].AttributesJson);
    }

    [Fact]
    public async Task PersistenceCountersWaitForReceiptAndNeverConfirmMessages()
    {
        var id = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        var rule = new MqTopicOptions { Name = "logs", Format = "raw" };
        var instance = new MqInstanceOptions { Id = id, Topics = [rule] };
        var buffer = new ReceiptBuffer();
        MqRuntime.Start(id, [("logs", "exact")]);
        MqRuntime.Connected(id);
        try
        {
            var pending = MqConsumerSupport.PersistAsync(instance, rule, [new LogRecord { Body = "test" }], buffer, CancellationToken.None);
            var before = MqRuntime.Snapshot().Single(x => x.InstanceId == id);
            Assert.Equal("persisting", before.State);
            Assert.Equal(0, before.PersistedRecords);
            Assert.Equal(0, before.Topics[0].ConfirmedMessages);
            buffer.Receipt.SetResult();
            await pending;
            var after = MqRuntime.Snapshot().Single(x => x.InstanceId == id);
            Assert.Equal(1, after.PersistedRecords);
            Assert.Equal(0, after.Topics[0].ConfirmedMessages);
            buffer.Receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = MqConsumerSupport.PersistAsync(instance, rule, [new LogRecord()], buffer, CancellationToken.None);
            buffer.Receipt.SetException(new IOException("disk"));
            await Assert.ThrowsAsync<IOException>(() => failed);
            Assert.Equal(1, MqRuntime.Snapshot().Single(x => x.InstanceId == id).PersistedRecords);
            using var stop = new CancellationTokenSource();
            buffer.Receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = MqConsumerSupport.PersistAsync(instance, rule, [new LogRecord()], buffer, stop.Token);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.Equal(0, MqRuntime.Snapshot().Single(x => x.InstanceId == id).Topics[0].ConfirmedMessages);
        }
        finally
        {
            MqRuntime.Remove(id);
        }
    }

    [Fact]
    public void DiagnosticsPreserveFailureUntilParsingRecovers()
    {
        var id = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        var rule = new MqTopicOptions { Name = "logs", Format = "json" };
        var instance = new MqInstanceOptions { Id = id, Topics = [rule] };
        MqRuntime.Start(id, [("logs", "exact")]);
        MqRuntime.Connected(id);
        try
        {
            var first = MqConsumerSupport.Decoded(instance, rule, true, "secret payload LineNumber: 0 | BytePositionInLine: 141");
            var second = MqConsumerSupport.Decoded(instance, rule, true, "other secret");
            var failure = Assert.Single(MqRuntime.Snapshot().Single(x => x.InstanceId == id).Topics[0].Failures);
            Assert.Equal(second, failure.Id);
            Assert.NotEqual(first, second);
            Assert.DoesNotContain("secret", failure.Reason);
            Assert.Equal(2, failure.Occurrences);
            MqRuntime.Decoded(id, "logs", "exact", false);
            failure = Assert.Single(MqRuntime.Snapshot().Single(x => x.InstanceId == id).Topics[0].Failures);
            Assert.Equal(second, failure.Id);
            Assert.NotNull(failure.RecoveredAt);
            MqRuntime.ConfirmationPending(id, "logs", "exact", 2);
            MqRuntime.ConfirmationFailed(id, "logs", "exact");
            MqRuntime.Confirmed(id, "logs", "exact", 1);
            var topic = MqRuntime.Snapshot().Single(x => x.InstanceId == id).Topics[0];
            Assert.Equal("confirm", topic.Stage);
            Assert.Equal(1, topic.PendingConfirmation);
            Assert.Contains(topic.Failures, x => x.Stage == "confirm");
        }
        finally
        {
            MqRuntime.Remove(id);
        }
    }

    private sealed class ReceiptBuffer : IIngestBuffer
    {
        public TaskCompletionSource Receipt { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PersistLogsAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken) => Receipt.Task.WaitAsync(cancellationToken);
        public bool TryEnqueueLog(LogRecord record) => false;
        public bool TryEnqueueSpan(SpanRecord record) => false;
        public bool TryEnqueueMetric(MetricPointRecord record) => false;
        public bool TryEnqueueTelemetry(TelemetryRecord record) => false;
        public bool TryEnqueueRumEvent(RumEventRecord record) => false;
        public bool TryEnqueueRumSession(RumSessionRecord record) => false;
        public bool TryEnqueueRumReplay(RumReplayRecord record) => false;
        public IngestBufferStats GetStats() => new();
    }
}
