using Microsoft.Extensions.Logging.Abstractions;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Infra.Services;
using SData.Store.Core;
using SData.Store.Models;
using Xunit;

namespace SData.Store.Tests;

/// <summary>真实有界缓冲与持久化完成回执.</summary>
[Collection("content-root-serial")]
public class MqPersistenceTests
{
    [Fact]
    public async Task ReceiptCompletesOnlyAfterWriterCommits()
    {
        var root = Path.Combine(Path.GetTempPath(), "sdata-mq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppConst.ContentRoot = root;
        var options = new SystemOptions();
        options.DuckDb.Path = "receipt.duckdb";
        options.DuckDb.Threads = 2;
        options.Ingest.FlushIntervalMs = 10;
        var buffer = new IngestBuffer(options);
        await using var manager = new DuckDbConnectionManager(options, NullLogger<DuckDbConnectionManager>.Instance);
        await manager.InitializeAsync();
        using var writer = new IngestWriterService(buffer, manager, options, new DefaultIdProvider(1), NullLogger<IngestWriterService>.Instance);
        var now = DateTimeOffset.UtcNow;
        var receipt = buffer.PersistLogsAsync(new[] { new LogRecord { Timestamp = now, Body = "receipt", Source = "kafka:test" } }, CancellationToken.None);
        Assert.False(receipt.IsCompleted);
        await writer.StartAsync(CancellationToken.None);
        await receipt.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, await manager.CountAsync(TimeTables.Name(TimeTables.LogsPrefix, now)));
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConsumptionSamplesRoundTripAcrossDaysWithBoundParameters()
    {
        var root = Path.Combine(Path.GetTempPath(), "sdata-mq-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppConst.ContentRoot = root;
        var options = new SystemOptions();
        options.DuckDb.Path = "statistics.duckdb";
        options.DuckDb.Threads = 2;
        var buffer = new IngestBuffer(options);
        await using var manager = new DuckDbConnectionManager(options, NullLogger<DuckDbConnectionManager>.Instance);
        await manager.InitializeAsync();
        using var writer = new IngestWriterService(buffer, manager, options, new DefaultIdProvider(1), NullLogger<IngestWriterService>.Instance);
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        await manager.EnsureDayTablesAsync(TimeTables.MqConsumptionPrefix, [today.AddDays(-1)]);
        var legacyTable = TimeTables.Name(TimeTables.MqConsumptionPrefix, today.AddDays(-1));
        await manager.ExecuteWriteAsync($"ALTER TABLE {legacyTable} DROP COLUMN diagnostics");
        var bootstrapper = new DuckDbSchemaBootstrapper(manager, NullLogger<DuckDbSchemaBootstrapper>.Instance);
        await bootstrapper.ApplyAsync();
        await bootstrapper.ApplyAsync();
        var records = new[]
        {
            new MqConsumptionRecord { Timestamp = today.AddSeconds(-1), InstanceId = 1, SessionId = "first", Rule = "logs'quoted", ReceivedMessages = 10, PersistedRecords = 20, ParseFailures = 2, SamplingGaps = 1, DiagnosticsJson = "{\"stage\":\"parse\"}" },
            new MqConsumptionRecord { Timestamp = today.AddSeconds(1), InstanceId = 1, SessionId = "second", Rule = "logs'quoted", ReceivedMessages = 3, PersistedRecords = 6 },
            new MqConsumptionRecord { Timestamp = today.AddSeconds(1), InstanceId = 2, SessionId = "other", Rule = "other" },
        };
        await writer.FlushAsync(records.Select(record => new IngestItem(IngestKind.MqConsumption, record)).ToList(), CancellationToken.None);
        var store = new MqConsumptionStore(manager);
        var result = await store.QueryAsync(today.AddDays(-1), today.AddDays(1), 1, CancellationToken.None);
        Assert.Equal(2, result.Count);
        Assert.Equal("second", result[0].SessionId);
        Assert.Equal("logs'quoted", result[1].Rule);
        Assert.Equal(2, result[1].ParseFailures);
        Assert.Equal(1, result[1].SamplingGaps);
        Assert.Equal("{\"stage\":\"parse\"}", result[1].DiagnosticsJson);
        Assert.Equal(3, (await store.QueryAsync(today.AddDays(-1), today.AddDays(1), null, CancellationToken.None)).Count);
        Assert.Empty(await store.QueryAsync(today.AddDays(2), today.AddDays(3), null, CancellationToken.None));
    }

    [Fact]
    public void WriterFailureIsSafeAndRecoveryKeepsFailureTime()
    {
        var buffer = new IngestBuffer(new SystemOptions());
        buffer.WriterFailed(new IOException("private payload and credentials"));
        var failure = buffer.GetStats().WriterFailure!;
        Assert.Equal("storage_io", failure.Code);
        Assert.DoesNotContain("private", failure.Reason);
        Assert.Null(failure.RecoveredAt);
        buffer.WriterRecovered();
        Assert.Equal(failure.At, buffer.GetStats().WriterFailure!.At);
        Assert.NotNull(buffer.GetStats().WriterFailure!.RecoveredAt);
    }

    [Fact]
    public async Task FailedBatch_RetriesAtomically_AndCompletesReceiptsOnlyAfterCommit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdata-mq-retry-" + Guid.NewGuid().ToString("N"));
        var options = new SystemOptions();
        options.DuckDb.Path = Path.Combine(directory, "retry.duckdb");
        options.Ingest.FlushIntervalMs = 10;
        var buffer = new IngestBuffer(options);
        await using var manager = new DuckDbConnectionManager(options, NullLogger<DuckDbConnectionManager>.Instance);
        await manager.InitializeAsync();
        var ids = new FailOnceIdProvider();
        using var writer = new IngestWriterService(buffer, manager, options, ids, NullLogger<IngestWriterService>.Instance);
        var now = DateTimeOffset.UtcNow;
        var logs = new[] { new LogRecord { Timestamp = now, Body = "first" }, new LogRecord { Timestamp = now, Body = "second" } };
        var receipt = buffer.PersistLogsAsync(logs, CancellationToken.None);
        await writer.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (buffer.GetStats().WriterFailure == null)
                await Task.Delay(10, timeout.Token);
            Assert.False(receipt.IsCompleted);
            Assert.Equal(0, await manager.CountAsync(TimeTables.Name(TimeTables.LogsPrefix, now)));
            Assert.Equal(100, logs[0].Id);
            await receipt.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(100, logs[0].Id);
            Assert.Equal(102, logs[1].Id);
            Assert.Equal(2, await manager.CountAsync(TimeTables.Name(TimeTables.LogsPrefix, now)));
            Assert.Equal(2, buffer.GetStats().Processed["log"]);
            Assert.NotNull(buffer.GetStats().WriterFailure!.RecoveredAt);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
            await manager.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("Out of Memory Error: private payload", "storage_memory")]
    [InlineData("FATAL Error: Out of Memory private payload", "database_invalidated")]
    [InlineData("database has been invalidated private payload", "database_invalidated")]
    [InlineData("IO Error: private payload", "storage_io")]
    [InlineData("unknown private payload", "batch_write_failed")]
    public void WriterFailure_ClassifiesDuckDbWithoutExposingRawErrors(string message, string expected)
    {
        var buffer = new IngestBuffer(new SystemOptions());
        using var connection = new DuckDB.NET.Data.DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT error($message)";
        command.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter("message", message));
        var failure = Assert.Throws<DuckDB.NET.Data.DuckDBException>(() => command.ExecuteScalar());
        buffer.WriterFailed(new InvalidOperationException("wrapper", failure));
        Assert.Equal(expected, buffer.GetStats().WriterFailure!.Code);
        Assert.DoesNotContain("private", buffer.GetStats().WriterFailure!.Reason);
    }

    private sealed class FailOnceIdProvider : IIdProvider
    {
        private int _calls;
        public long GetLongId()
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 2) throw new IOException("controlled transient failure");
            return 99 + call;
        }
    }

    [Fact]
    public void ConcurrentFullBufferNeverReportsDroppedWritesAsAccepted()
    {
        var options = new SystemOptions();
        options.Ingest.BufferCapacity = 8;
        var buffer = new IngestBuffer(options);
        var accepted = 0;
        Parallel.For(0, 1000, _ =>
        {
            if (buffer.TryEnqueueLog(new LogRecord()))
            {
                Interlocked.Increment(ref accepted);
            }
        });
        Assert.Equal(8, accepted);
        Assert.Equal(8, buffer.Reader.Count);
        Assert.Equal(992, buffer.GetStats().Dropped["log"]);
    }

    [Fact]
    public async Task CancellationDoesNotPretendPendingWriteWasPersisted()
    {
        var buffer = new IngestBuffer(new SystemOptions());
        using var stop = new CancellationTokenSource();
        var receipt = buffer.PersistLogsAsync(new[] { new LogRecord() }, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receipt);
    }
}
