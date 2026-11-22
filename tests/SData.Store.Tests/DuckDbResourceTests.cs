using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Infra.Configuration;
using SData.Infra.Services;
using SData.Settings.Services;
using SData.Store.Core;
using SData.Store.Models;
using SData.Store.QueryDsl;
using Xunit;

namespace SData.Store.Tests;

public class DuckDbResourceTests
{
    [Fact]
    public async Task QueryAdmission_CoversReaderLifetime_AndCancellationDoesNotLeakPermit()
    {
        await using var harness = await Harness.CreateAsync();
        using var lease = await harness.Manager.LockQueryAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var waiting = harness.Manager.QueryAsync("SELECT 42", null, async reader =>
        {
            await reader.ReadAsync();
            return reader.GetInt32(0);
        }, stop.Token);
        Assert.False(waiting.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        lease.Dispose();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = harness.Manager.QueryAsync("SELECT 1", null, async reader =>
        {
            entered.SetResult();
            await release.Task;
            return 1;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = harness.Manager.LockQueryAsync(CancellationToken.None);
        Assert.False(next.IsCompleted);
        release.SetResult();
        Assert.Equal(1, await first);
        using var nextLease = await next.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task QueryFailure_ReleasesPermit_AndDefaultThreadsAreBounded()
    {
        await using var harness = await Harness.CreateAsync();
        await Assert.ThrowsAsync<DuckDBException>(() => harness.Manager.QueryAsync("SELECT missing_column", null, _ => Task.FromResult(0)));
        var threads = await harness.Manager.QueryAsync("SELECT current_setting('threads')", null, async reader =>
        {
            await reader.ReadAsync();
            return reader.GetInt64(0);
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, threads);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task QueryBudget_RespectsConfiguredParallelism(int limit)
    {
        await using var harness = await Harness.CreateAsync(options => options.DuckDb.MaxConcurrentQueries = limit);
        var leases = new List<IDisposable>();
        try
        {
            for (var i = 0; i < limit; i++)
                leases.Add(await harness.Manager.LockQueryAsync(CancellationToken.None));
            var waiting = harness.Manager.LockQueryAsync(CancellationToken.None);
            Assert.False(waiting.IsCompleted);
            leases[0].Dispose();
            using var next = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    [Fact]
    public async Task MaintenanceBudget_SerializesAndCancellationReleasesPermit()
    {
        await using var harness = await Harness.CreateAsync();
        using var lease = await harness.Manager.LockMaintenanceAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var waiting = harness.Manager.LockMaintenanceAsync(stop.Token);
        Assert.False(waiting.IsCompleted);

        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync([
            new(IngestKind.Log, new LogRecord { Timestamp = now, Body = "maintenance gate does not block ingestion" })
        ], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        lease.Dispose();

        using var next = await harness.Manager.LockMaintenanceAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var histogram = harness.Manager.Snapshot.Histograms
            .Single(item => item.Name == "sdata_store_maintenance_lock_wait_seconds");
        Assert.True(histogram.Count > 0);
        Assert.Equal(histogram.Count, histogram.BucketCounts[^1]);
    }

    [Fact]
    public async Task AlertAndUserQueryUseSeparateAdmissionSlots()
    {
        await using var harness = await Harness.CreateAsync();
        using var alertLease = await harness.Manager.LockAlertQueryAsync(CancellationToken.None);

        using (var userLease = await harness.Manager.LockQueryAsync(CancellationToken.None)
                   .WaitAsync(TimeSpan.FromSeconds(5)))
        {
            using var stop = new CancellationTokenSource();
            var waitingAlert = harness.Manager.LockAlertQueryAsync(stop.Token);
            Assert.False(waitingAlert.IsCompleted);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingAlert);
        }

        var histogram = harness.Manager.Snapshot.Histograms
            .Single(item => item.Name == "sdata_store_alert_query_lock_wait_seconds");
        Assert.True(histogram.Count > 0);
        Assert.Equal(histogram.Count, histogram.BucketCounts[^1]);
    }

    [Fact]
    public async Task TraceIndex_ExistsOnHotTableAndRemovedOnFreeze()
    {
        await using var harness = await Harness.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await harness.Manager.EnsureDayTablesAsync(TimeTables.TracesPrefix, [now]);
        var table = TimeTables.Name(TimeTables.TracesPrefix, now);
        var indexName = table + "_tid_idx";

        var hotCount = await harness.Manager.QueryAsync(
            "SELECT count(*) FROM duckdb_indexes() WHERE table_name = $table_name AND index_name = $index_name",
            [
                new DuckDB.NET.Data.DuckDBParameter("table_name", table),
                new DuckDB.NET.Data.DuckDBParameter("index_name", indexName),
            ],
            async reader =>
            {
                await reader.ReadAsync();
                return reader.GetInt64(0);
            });
        Assert.Equal(1, hotCount);

        var snapshot = await harness.Manager.FreezeDayAsync(TimeTables.TracesPrefix, now, CancellationToken.None);
        Assert.NotNull(snapshot);

        var frozenCount = await harness.Manager.QueryAsync(
            "SELECT count(*) FROM duckdb_indexes() WHERE table_name = $table_name",
            [new DuckDB.NET.Data.DuckDBParameter("table_name", snapshot)],
            async reader =>
            {
                await reader.ReadAsync();
                return reader.GetInt64(0);
            });
        Assert.Equal(0, frozenCount);
    }

    [Fact]
    public async Task AnalyticsAndDsl_ShareQueryBudget_WithoutBlockingWriter()
    {
        await using var harness = await Harness.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await harness.Manager.EnsureDayTablesAsync(TimeTables.LogsPrefix, [now]);
        var resolver = new HotResolver(harness.Manager);
        var analytics = new AnalyticsExecutor(harness.Manager, harness.Options, SystemSettingsProvider.FileOnly(harness.Options), resolver, NullLogger<AnalyticsExecutor>.Instance);
        var dsl = new QueryDslExecutorService(harness.Manager, SystemSettingsProvider.FileOnly(harness.Options), resolver);
        using var lease = await harness.Manager.LockQueryAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var sql = analytics.ExecuteAsync("SELECT 1", cancellationToken: stop.Token);
        var search = analytics.ExecuteSearchAsync("SELECT * FROM {{logs}} WHERE ts >= $start AND ts < $end", now.AddMinutes(-1), now.AddMinutes(1), cancellationToken: stop.Token);
        var alert = analytics.ExecuteAlertCountAsync(new AlertCountQuery("logs", "", new Dictionary<string, object?>(), now.AddMinutes(-1), now, 10), stop.Token);
        var structured = dsl.ExecuteAsync(new QueryDslModel { Range = new QueryRange { From = now.AddMinutes(-1), To = now.AddMinutes(1) } }, DataDomain.Logs, stop.Token);
        Assert.False(sql.IsCompleted);
        Assert.False(search.IsCompleted);
        Assert.False(alert.IsCompleted);
        Assert.False(structured.IsCompleted);

        await harness.Writer.FlushAsync([new(IngestKind.Log, new LogRecord { Timestamp = now, Body = "query gate does not block ingestion" })], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sql);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => alert);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => structured);
        lease.Dispose();
        Assert.Single((await analytics.ExecuteAsync("SELECT 42").WaitAsync(TimeSpan.FromSeconds(5))).Rows);
    }

    [Fact]
    public async Task Writer_WaitsForSharedWriteGate_AndCancellationReleasesTableGate()
    {
        await using var harness = await Harness.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await harness.Manager.EnsureDayTablesAsync(TimeTables.LogsPrefix, [now]);
        var batch = new List<IngestItem> { new(IngestKind.Log, new LogRecord { Timestamp = now, Body = "write gate" }) };
        using var lease = await harness.Manager.LockWriteAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var flush = harness.Writer.FlushAsync(batch, stop.Token);
        var checkpoint = harness.Manager.ExecuteWriteAsync("CHECKPOINT", cancellationToken: stop.Token);
        Assert.False(flush.IsCompleted);
        Assert.False(checkpoint.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkpoint);
        lease.Dispose();
        await harness.Writer.FlushAsync(batch, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await harness.Manager.CountAsync(TimeTables.Name(TimeTables.LogsPrefix, now)));
    }

    [Fact]
    public async Task LogLayoutCache_IsReused_AndInvalidatedAfterFreezeAndDrop()
    {
        await using var harness = await Harness.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var table = TimeTables.Name(TimeTables.LogsPrefix, now);
        await harness.Manager.EnsureDayTablesAsync(TimeTables.LogsPrefix, [now]);
        using var connection = harness.Manager.CloneConnection();
        var first = await harness.Manager.GetLogColumnsAsync(connection, table, CancellationToken.None);
        Assert.Same(first, await harness.Manager.GetLogColumnsAsync(connection, table, CancellationToken.None));
        var snapshot = await harness.Manager.FreezeDayAsync(TimeTables.LogsPrefix, now, CancellationToken.None);
        Assert.NotNull(snapshot);
        await harness.Manager.EnsureDayTablesAsync(TimeTables.LogsPrefix, [now]);
        var second = await harness.Manager.GetLogColumnsAsync(connection, table, CancellationToken.None);
        Assert.NotSame(first, second);
        await harness.Manager.DropTableAsync(table);
        await harness.Manager.EnsureDayTablesAsync(TimeTables.LogsPrefix, [now]);
        Assert.NotSame(second, await harness.Manager.GetLogColumnsAsync(connection, table, CancellationToken.None));
        await harness.Writer.FlushAsync([new(IngestKind.Log, new LogRecord { Timestamp = now, Body = "after rebuild" })], CancellationToken.None);
        Assert.Equal(1, await harness.Manager.CountAsync(table));
    }

    [Fact]
    public async Task RealMemoryFailure_IsClassified_AndDatabaseRemainsUsableAfterBudgetRestored()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Manager.ExecuteWriteAsync("SET memory_limit='1MB'");
        var failure = await Assert.ThrowsAsync<DuckDBException>(() => harness.Manager.QueryAsync(
            "SELECT i, count(*) FROM range(1000000) r(i) GROUP BY i", null, _ => Task.FromResult(0)));
        harness.Buffer.WriterFailed(failure);
        Assert.Equal("storage_memory", harness.Buffer.GetStats().WriterFailure!.Code);
        await harness.Manager.ExecuteWriteAsync("SET memory_limit='128MB'");
        var now = DateTimeOffset.UtcNow;
        await harness.Writer.FlushAsync([new(IngestKind.Log, new LogRecord { Timestamp = now, Body = "after memory failure" })], CancellationToken.None);
        Assert.Equal(1, await harness.Manager.CountAsync(TimeTables.Name(TimeTables.LogsPrefix, now)));
    }

    [Fact]
    public async Task MixedWorkload_RepeatedQueriesRollupAndCheckpointRemainUsable()
    {
        await using var harness = await Harness.CreateAsync();
        await new DuckDbSchemaBootstrapper(harness.Manager, NullLogger<DuckDbSchemaBootstrapper>.Instance).ApplyAsync();
        var analytics = new AnalyticsExecutor(harness.Manager, harness.Options, SystemSettingsProvider.FileOnly(harness.Options), new HotResolver(harness.Manager), NullLogger<AnalyticsExecutor>.Instance);
        var rollup = new RollupService(harness.Manager, NullLogger<RollupService>.Instance);
        var time = DateTimeOffset.UtcNow.AddMinutes(-10);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var maximumTracked = 0L;
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var batch = Enumerable.Range(0, 100).Select(index => new IngestItem(IngestKind.Log,
                new LogRecord { Timestamp = time, Body = "mixed-" + index })).ToList();
            batch.Add(new(IngestKind.Metric, new MetricPointRecord { Name = "mixed", Timestamp = time, Value = iteration }));
            await Task.WhenAll(
                harness.Writer.FlushAsync(batch, timeout.Token),
                analytics.ExecuteAsync("SELECT i, repeat(md5(i::VARCHAR), 32) FROM range(500000) r(i)", 10, timeout.Token),
                Task.Run(() => rollup.BackfillAsync(timeout.Token), timeout.Token),
                harness.Manager.ExecuteWriteAsync("CHECKPOINT", cancellationToken: timeout.Token));
            var tracked = await harness.Manager.QueryAsync("SELECT CAST(sum(memory_usage_bytes) AS BIGINT) FROM duckdb_memory()", null,
                async reader => { await reader.ReadAsync(); return reader.GetInt64(0); }, timeout.Token);
            maximumTracked = Math.Max(maximumTracked, tracked);
        }
        Assert.Equal(2000, await harness.Manager.CountAsync(TimeTables.Name(TimeTables.LogsPrefix, time), timeout.Token));
        Assert.InRange(maximumTracked, 0, 128_000_000);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60));
        Assert.Single((await analytics.ExecuteAsync("SELECT 42", cancellationToken: timeout.Token)).Rows);
    }

    private sealed class HotResolver(DuckDbConnectionManager manager) : ITableResolver
    {
        public Task<ResolvedTables> ResolveAsync(DataDomain domain, DateTimeOffset from, DateTimeOffset to, string? searchKeyword = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ResolvedTables(manager.GetDayTables(TimeTables.LogsPrefix, from, to), []));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "sdata-resources-" + Guid.NewGuid().ToString("N"));
        public SystemOptions Options { get; } = new();
        public IngestBuffer Buffer { get; private set; } = null!;
        public DuckDbConnectionManager Manager { get; private set; } = null!;
        public IngestWriterService Writer { get; private set; } = null!;

        public static async Task<Harness> CreateAsync(Action<SystemOptions>? configure = null)
        {
            var harness = new Harness();
            harness.Options.DuckDb.Path = Path.Combine(harness._directory, "resources.duckdb");
            harness.Options.DuckDb.MemoryLimit = "128MB";
            configure?.Invoke(harness.Options);
            harness.Buffer = new IngestBuffer(harness.Options);
            harness.Manager = new DuckDbConnectionManager(harness.Options, NullLogger<DuckDbConnectionManager>.Instance);
            await harness.Manager.InitializeAsync();
            harness.Writer = new IngestWriterService(harness.Buffer, harness.Manager, SystemSettingsProvider.FileOnly(harness.Options), new DefaultIdProvider(1), NullLogger<IngestWriterService>.Instance);
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            Writer.Dispose();
            await Manager.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }
}
