using global::Xunit;
using SData.Store;
using SData.Settings.Services;
using SData.Store.Core;
using SData.Store.Models;
using SData.Store.QueryDsl;
using Microsoft.Extensions.Logging.Abstractions;

namespace SData.Store.Tests;

public partial class ArchiveTests
{
    [Fact]
    public async Task Archive_AllDomains_ColdQueriesAndLateDataRemainReadable()
    {
        using var harness = new ArchiveHarness(options =>
        {
            options.Retention.Logs.HotDays = 1;
            options.Retention.Traces.HotDays = 1;
            options.Retention.Metrics.HotDays = 1;
            options.Retention.Rum.HotDays = 1;
        });
        var day = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).AddDays(-1);
        var stamp = day.AddHours(10);
        await harness.Writer.FlushAsync([
            new(IngestKind.Log, new LogRecord { Timestamp = stamp, Service = "svc", Severity = "INFO", Body = "archive" }),
            new(IngestKind.Trace, new SpanRecord { Timestamp = stamp, TraceId = "trace", SpanId = "span", Service = "svc", Name = "span", Kind = "server", Status = "ok", DurationUs = 100 }),
            new(IngestKind.Metric, new MetricPointRecord { Timestamp = stamp, Name = "cpu", Value = 12, Kind = "gauge" }),
            new(IngestKind.RumSession, new RumSessionRecord { Timestamp = stamp, ProjectKey = "project", SessionId = "session", Domain = "site" }),
            new(IngestKind.RumEvent, new RumEventRecord { Timestamp = stamp.AddMinutes(1), ProjectKey = "project", SessionId = "session", EventType = "error", EventName = "error", PagePath = "/home" }),
            new(IngestKind.RumReplay, new RumReplayRecord { Timestamp = stamp.AddMinutes(2), ProjectKey = "project", SessionId = "session", Seq = 0, BlobBase64 = "compressed-chunk" }),
        ], harness.Token);
        var rum = new RumStore(harness.Manager, harness.Resolver);
        var before = await rum.SearchSessionsAsync("project", day, day.AddHours(12), new RumSessionFilter(), harness.Token);
        Assert.Equal(6, await Pin(harness).Archive.RunOnceAsync(harness.Token));
        var after = await rum.SearchSessionsAsync("project", day, day.AddHours(12), new RumSessionFilter(), harness.Token);
        var session = Assert.Single(after.Items);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(1, session.PageViews);
        Assert.Equal(1, session.ErrorCount);
        Assert.True(session.HasReplay);
        Assert.Single(await rum.ListSessionEventsAsync("project", "session", day, day.AddHours(12), harness.Token));
        Assert.Equal("compressed-chunk", Assert.Single(await rum.GetReplayAsync("project", "session", day, day.AddHours(12), harness.Token)).BlobBase64);
        Assert.Empty((await rum.SearchSessionsAsync("other", day, day.AddHours(12), new RumSessionFilter(), harness.Token)).Items);
        var analytics = new AnalyticsExecutor(harness.Manager, harness.Options, SystemSettingsProvider.FileOnly(harness.Options), harness.Resolver, NullLogger<AnalyticsExecutor>.Instance);
        var cold = await analytics.ExecuteSearchAsync("SELECT count(*) AS n FROM {{sessions}} WHERE ts >= $start AND ts < $end", day, day.AddHours(12), cancellationToken: harness.Token);
        Assert.Equal(1L, Convert.ToInt64(cold.Rows.Single()[0]));
        var traces = new TraceStore(harness.Manager, SystemSettingsProvider.FileOnly(harness.Options), harness.Resolver);
        Assert.Single(await traces.GetTraceAsync("trace", harness.Token));
        var metrics = new MetricStore(harness.Manager, harness.Options, harness.Resolver);
        Assert.Contains("cpu", await metrics.ListNamesAsync(harness.Token));
        var series = await metrics.QueryRangeAsync(new MetricSeriesQuery { Name = "cpu", Start = day, End = day.AddHours(12) }, harness.Token);
        Assert.Equal(12, Assert.Single(Assert.Single(series.SeriesList).Points).Value);

        // 已归档日期补报，热增量与既有冷对象同时可查。
        await harness.Writer.FlushAsync([
            new(IngestKind.RumEvent, new RumEventRecord { Timestamp = stamp.AddMinutes(3), ProjectKey = "project", SessionId = "session", EventType = "error", EventName = "late" }),
        ], harness.Token);
        Assert.Equal(2, (await rum.ListSessionEventsAsync("project", "session", day, day.AddHours(12), harness.Token)).Count);
        Assert.Equal(1, await Pin(harness).Archive.RunOnceAsync(harness.Token));
        Assert.Equal(2, (await rum.ListSessionEventsAsync("project", "session", day, day.AddHours(12), harness.Token)).Count);
        Assert.Equal(2, (await harness.GetLedgerAsync("rum_event")).Count);
        Assert.Equal(0, await Pin(harness).Archive.RunOnceAsync(harness.Token));
    }

    [Fact]
    public async Task Archive_FrozenBatchRecovery_PreservesNewHotData()
    {
        using var harness = new ArchiveHarness(options => options.Retention.Logs.HotDays = 1);
        var day = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).AddDays(-2);
        await harness.CreateDayTableWithRowsAsync(TimeTables.LogsPrefix, day, 2);
        var snapshot = await harness.Manager.FreezeDayAsync(TimeTables.LogsPrefix, day, harness.Token);
        Assert.NotNull(snapshot);
        await harness.Writer.FlushAsync([new(IngestKind.Log, new LogRecord { Timestamp = day.AddHours(2), Service = "late", Severity = "INFO", Body = "late" })], harness.Token);
        Assert.Equal(2, harness.Manager.GetDayTables(TimeTables.LogsPrefix, day, day.AddDays(1)).Count);
        Assert.Equal(2, await Pin(harness).Archive.RunOnceAsync(harness.Token));
        Assert.Equal(3, (await harness.GetLedgerAsync("logs")).Sum(x => x.RowCount));
        Assert.Equal(2, (await harness.Resolver.ResolveAsync(DataDomain.Logs, day, day.AddHours(12))).ColdFiles.Count);
    }

    [Fact]
    public async Task Archive_FailedDestination_RetainsBatchAndRetries()
    {
        using var harness = new ArchiveHarness(options => options.Retention.Logs.HotDays = 1);
        var day = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).AddDays(-2);
        await harness.CreateDayTableWithRowsAsync(TimeTables.LogsPrefix, day, 2);
        var blocker = Path.Combine(harness.TempDirectory, "data", "cold", "logs");
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        await File.WriteAllTextAsync(blocker, "block directory creation");
        await Assert.ThrowsAnyAsync<IOException>(() => Pin(harness).Archive.RunOnceAsync(harness.Token));
        Assert.Empty(await harness.GetLedgerAsync("logs"));
        var batch = Assert.Single(harness.Manager.GetDayTables(TimeTables.LogsPrefix, day, day.AddDays(1)));
        Assert.True(TimeTables.IsArchive(batch));
        Assert.Equal(2, await harness.Manager.CountAsync(batch, harness.Token));
        File.Delete(blocker);
        Assert.Equal(1, await Pin(harness).Archive.RunOnceAsync(harness.Token));
        Assert.Equal(2, Assert.Single(await harness.GetLedgerAsync("logs")).RowCount);
    }

    [Fact]
    public async Task Archive_PublishedBatchRestart_DoesNotDuplicateOrRepublish()
    {
        using var harness = new ArchiveHarness(options => options.Retention.Logs.HotDays = 1);
        var day = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).AddDays(-2);
        await harness.CreateDayTableWithRowsAsync(TimeTables.LogsPrefix, day, 2);
        Assert.Equal(1, await Pin(harness).Archive.RunOnceAsync(harness.Token));
        var row = Assert.Single(await harness.GetLedgerAsync("logs"));
        var batchId = row.ObjectKey.Replace('\\', '/').Split('/')[^2];
        var snapshot = $"{TimeTables.LogsPrefix}archive_{batchId}_{day:yyyyMMdd}";
        var source = Path.Combine(harness.TempDirectory, row.ObjectKey).Replace('\\', '/').Replace("'", "''");
        // 模拟台账提交后、热副本清理前进程退出。
        await harness.Manager.ExecuteWriteAsync($"CREATE TABLE {snapshot} AS SELECT * FROM read_parquet('{source}')");
        await harness.Manager.DisposeAsync();

        await using var restarted = new DuckDbConnectionManager(harness.Options, NullLogger<DuckDbConnectionManager>.Instance);
        await restarted.InitializeAsync();
        var scopes = new SingleScopeFactory(harness.Ledger);
        var cache = new ColdLedgerCache(scopes, NullLogger<ColdLedgerCache>.Instance);
        var objects = new LocalObjectStore();
        using var coldCache = new ColdCacheService(SystemSettingsProvider.FileOnly(harness.Options), objects, NullLogger<ColdCacheService>.Instance);
        var resolver = new TableResolver(restarted, harness.Options, harness.Settings, cache, coldCache, NullLogger<TableResolver>.Instance);
        var resolved = await resolver.ResolveAsync(DataDomain.Logs, day, day.AddHours(12));
        Assert.Empty(resolved.LocalTables);
        Assert.Single(resolved.ColdFiles);
        using var archive = new ArchiveService(restarted, harness.Options, harness.Settings, cache, scopes, NullLogger<ArchiveService>.Instance);
        await archive.RunOnceAsync(harness.Token);
        Assert.Single(await harness.GetLedgerAsync("logs"));
        Assert.Empty(restarted.GetManagedTables(TimeTables.LogsPrefix));
    }

    [Fact]
    public async Task Archive_HandoffWaitsForReaders_AndCancellationReleasesGate()
    {
        using var harness = new ArchiveHarness();
        using var reader = await harness.Manager.ReadSourcesAsync(harness.Token);
        using var timeout = new CancellationTokenSource();
        var change = harness.Manager.ChangeSourcesAsync(timeout.Token);
        Assert.False(change.IsCompleted);
        await timeout.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await change);
        reader.Dispose();
        using var next = await harness.Manager.ChangeSourcesAsync(harness.Token);
    }
}
