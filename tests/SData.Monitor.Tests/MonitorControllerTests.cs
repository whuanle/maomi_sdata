using global::Xunit;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SData.Monitor.Api;
using SData.Store;
using System.Globalization;

namespace SData.Monitor.Tests;

public class MonitorControllerTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/api/health")]
    public void Health_SupportsProbeAndApiClientRoutes(string route)
    {
        var method = typeof(MonitorController).GetMethod(nameof(MonitorController.Health))!;
        var routes = method.GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>();
        Assert.Contains(routes, attribute => attribute.Template == route);
    }

    [Fact]
    public async Task Metrics_ReportsGcAndAvailableStorageSamples()
    {
        var storage = new StorageMetrics
        {
            Local = new LocalStorageSnapshot
            {
                State = "ok",
                DuckDbState = "ok", TrackedMemoryBytes = 1234,
                FileState = "ok", WalBytes = 567,
            },
        };
        using var services = new ServiceCollection()
            .AddMediator()
            .AddSingleton<IRequestHandler<QueryStatusCommand, StatusResponse>>(new StatusHandler())
            .BuildServiceProvider();
        var performance = new PerformanceMetrics();
        var controller = new MonitorController(new global::Mediator.Mediator(services), null!, storage, performance);

        var metrics = await controller.Metrics(CancellationToken.None);
        Assert.Contains("sdata_duckdb_tracked_memory_bytes 1234\n", metrics);
        Assert.Contains("sdata_duckdb_wal_bytes 567\n", metrics);
        Assert.Contains("sdata_store_write_lock_wait_seconds_bucket{le=\"+Inf\"} 3\n", metrics);
        Assert.Contains("# TYPE sdata_store_maintenance_lock_wait_seconds histogram\n", metrics, StringComparison.Ordinal);
        Assert.Contains("# TYPE sdata_store_source_switch_lock_hold_seconds histogram\n", metrics, StringComparison.Ordinal);
        Assert.Contains("sdata_store_source_switch_lock_hold_seconds_count 1\n", metrics, StringComparison.Ordinal);
        Assert.Contains("sdata_archive_rows_total 12\n", metrics);
        var committed = MetricValue(metrics, "sdata_gc_committed_bytes");
        var paused = MetricValue(metrics, "sdata_gc_pause_seconds_total");
        Assert.True(committed >= 0);
        Assert.True(paused >= 0);

        storage.Local = storage.Local with { DuckDbState = "error", FileState = "error" };
        metrics = await controller.Metrics(CancellationToken.None);
        Assert.DoesNotContain("sdata_duckdb_tracked_memory_bytes", metrics);
        Assert.DoesNotContain("sdata_duckdb_wal_bytes", metrics);

        storage.Local = storage.Local with { State = "error", DuckDbState = "ok", FileState = "ok" };
        metrics = await controller.Metrics(CancellationToken.None);
        Assert.DoesNotContain("sdata_duckdb_tracked_memory_bytes", metrics);
        Assert.DoesNotContain("sdata_duckdb_wal_bytes", metrics);
    }

    private static double MetricValue(string metrics, string name)
    {
        var line = Assert.Single(metrics.Split('\n'), value => value.StartsWith(name + " ", StringComparison.Ordinal));
        return double.Parse(line[(name.Length + 1)..], CultureInfo.InvariantCulture);
    }

    private sealed class StatusHandler : IRequestHandler<QueryStatusCommand, StatusResponse>
    {
        public ValueTask<StatusResponse> Handle(QueryStatusCommand request, CancellationToken cancellationToken)
            => ValueTask.FromResult(new StatusResponse());
    }

    private sealed class StorageMetrics : IStorageMetrics
    {
        public LocalStorageSnapshot Local { get; set; } = new();
        public S3StorageSnapshot S3 { get; } = new();
    }

    private sealed class PerformanceMetrics : IStorePerformanceMetrics
    {
        public StorePerformanceSnapshot Snapshot { get; } = new()
        {
            Histograms =
            [
                new StoreHistogramSnapshot
                {
                    Name = "sdata_store_write_lock_wait_seconds",
                    UpperBoundsSeconds = [0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, double.PositiveInfinity],
                    BucketCounts = [0, 0, 1, 2, 2, 3, 3, 3, 3],
                    Count = 3,
                    SumSeconds = 0.012,
                },
                new StoreHistogramSnapshot
                {
                    Name = "sdata_store_source_switch_lock_hold_seconds",
                    UpperBoundsSeconds = [0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, double.PositiveInfinity],
                    BucketCounts = [0, 0, 0, 1, 1, 1, 1, 1, 1],
                    Count = 1,
                    SumSeconds = 0.025,
                },
                new StoreHistogramSnapshot
                {
                    Name = "sdata_store_maintenance_lock_wait_seconds",
                    UpperBoundsSeconds = [0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, double.PositiveInfinity],
                    BucketCounts = [0, 0, 1, 1, 1, 1, 1, 1, 1],
                    Count = 1,
                    SumSeconds = 0.008,
                },
            ],
            Counters = [new StoreCounterSnapshot { Name = "sdata_archive_rows_total", Value = 12 }],
        };

        public void RecordIngestFastRejected(long count)
        {
        }

        public void RecordIngestAppendRowLoop(double seconds)
        {
        }

        public void RecordIngestAppendClose(double seconds)
        {
        }

        public void RecordIngestAppendRows(long count)
        {
        }
    }
}
