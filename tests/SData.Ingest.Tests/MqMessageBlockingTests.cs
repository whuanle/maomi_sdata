using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Infra.Configuration;
using SData.Ingest.Consumers;
using SData.Ingest.Services;
using SData.Store;
using SData.Store.Models;
using Xunit;

namespace SData.Ingest.Tests;

[Collection("mq-runtime")]
public class MqMessageBlockingTests
{
    [Theory]
    [InlineData("json", "{bad")]
    [InlineData("json", "")]
    [InlineData("json", "[{\"body\":\"valid\"},42]")]
    [InlineData("json", "[]")]
    [InlineData("unsupported", "raw message")]
    [InlineData("regex", "unmatched")]
    public async Task InvalidMessageBlocksWithoutWritingOrConfirming(string format, string text)
    {
        var instance = Instance(format);
        var buffer = new Buffer();
        MqRuntime.Start(instance.Id, [("logs", "exact")]);
        MqRuntime.Connected(instance.Id);
        using var stop = new CancellationTokenSource();
        try
        {
            var processing = MqConsumerSupport.ProcessMessageAsync(instance, instance.Topics[0], Encoding.UTF8.GetBytes(text),
                "test:logs", buffer, new MqLogDecoder(new OtlpProtoParserService()), NullLogger.Instance, stop.Token);
            var item = MqRuntime.Snapshot().Single(x => x.InstanceId == instance.Id);
            var topic = Assert.Single(item.Topics);
            Assert.False(processing.IsCompleted);
            Assert.Equal(1, topic.BlockedMessages);
            Assert.NotNull(topic.BlockedSince);
            Assert.Equal("parse", topic.Stage);
            Assert.Equal(0, topic.PersistedRecords);
            Assert.Equal(0, topic.PendingConfirmation);
            Assert.Equal(0, topic.ConfirmedMessages);
            Assert.Equal(0, buffer.Writes);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
            Assert.Equal(0, buffer.Writes);
            Assert.Equal(0, MqRuntime.Snapshot().Single(x => x.InstanceId == instance.Id).Topics[0].BlockedMessages);
        }
        finally { MqRuntime.Remove(instance.Id); MqRetry.Remove(instance.Id); }
    }

    [Fact]
    public async Task RetriesOriginalPayloadAndReturnsOnlyAfterSuccessfulCommit()
    {
        var instance = Instance("raw");
        var buffer = new Buffer();
        var decoder = new RecoveringDecoder();
        var payload = Encoding.UTF8.GetBytes("original message");
        MqRuntime.Start(instance.Id, [("logs", "exact")]);
        MqRuntime.Connected(instance.Id);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var processing = MqConsumerSupport.ProcessMessageAsync(instance, instance.Topics[0], payload,
                "test:logs", buffer, decoder, NullLogger.Instance, stop.Token);
            Assert.Equal(0, buffer.Writes);
            await processing;
            Assert.Equal(2, decoder.Payloads.Count);
            Assert.All(decoder.Payloads, actual => Assert.Same(payload, actual));
            Assert.Equal(1, buffer.Writes);
            var topic = MqRuntime.Snapshot().Single(x => x.InstanceId == instance.Id).Topics[0];
            Assert.Equal(1, topic.ReceivedMessages);
            Assert.Equal(1, topic.ParseFailures);
            Assert.Equal(1, topic.PersistedRecords);
            Assert.Equal(0, topic.ConfirmedMessages);
            Assert.Equal(1, topic.PendingConfirmation);
            Assert.Equal(0, topic.BlockedMessages);
        }
        finally { MqRuntime.Remove(instance.Id); MqRetry.Remove(instance.Id); }
    }

    [Fact]
    public async Task PersistenceFailureRetriesSameMessageWithoutEarlyConfirmation()
    {
        var instance = Instance("raw");
        var buffer = new Buffer { FailFirst = true };
        MqRuntime.Start(instance.Id, [("logs", "exact")]);
        MqRuntime.Connected(instance.Id);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var processing = MqConsumerSupport.ProcessMessageAsync(instance, instance.Topics[0], Encoding.UTF8.GetBytes("original"),
                "test:logs", buffer, new MqLogDecoder(new OtlpProtoParserService()), NullLogger.Instance, stop.Token);
            var before = MqRuntime.Snapshot().Single(x => x.InstanceId == instance.Id).Topics[0];
            Assert.False(processing.IsCompleted);
            Assert.Equal(0, before.PendingConfirmation);
            Assert.Equal(0, before.PersistedRecords);
            await processing;
            Assert.Equal(2, buffer.Writes);
            var after = MqRuntime.Snapshot().Single(x => x.InstanceId == instance.Id).Topics[0];
            Assert.Equal(1, after.PersistedRecords);
            Assert.Equal(0, after.ConfirmedMessages);
            Assert.Equal(1, after.PendingConfirmation);
        }
        finally { MqRuntime.Remove(instance.Id); MqRetry.Remove(instance.Id); }
    }

    private static MqInstanceOptions Instance(string format) => new()
    {
        Id = Random.Shared.NextInt64(1_000_000, long.MaxValue), RetryInitialSeconds = 1, RetryMaxSeconds = 1,
        Topics = [new() { Name = "logs", Format = format }],
    };

    private sealed class RecoveringDecoder : IMqLogDecoder
    {
        public List<byte[]> Payloads { get; } = [];
        public bool TryDecode(byte[] payload, MqTopicOptions topic, string source, out List<LogRecord> records, out string? error)
        {
            Payloads.Add(payload);
            records = [new() { Body = "original message" }];
            error = Payloads.Count == 1 ? "bad format" : null;
            return true;
        }
    }

    private sealed class Buffer : IIngestBuffer
    {
        public int Writes;
        public bool FailFirst;
        public Task PersistLogsAsync(IReadOnlyList<LogRecord> records, CancellationToken token)
        {
            Writes++;
            return FailFirst && Writes == 1 ? Task.FromException(new IOException("disk")) : Task.CompletedTask;
        }
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
