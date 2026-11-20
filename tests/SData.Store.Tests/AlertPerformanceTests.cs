using System.Diagnostics;
using global::Xunit;
using SData.Alerts;
using SData.Infra.Configuration;
using SData.Store.Models;

namespace SData.Store.Tests;

public partial class AnalyticsSearchSqlTests
{
    [Fact]
    public async Task AlertPerformance_MixedSearchAndIngest_Sample()
    {
        using var harness = new SearchHarness();
        var end = DateTimeOffset.UtcNow;
        var records = Enumerable.Range(0, 100000).Select(i => new IngestItem(IngestKind.Log,
            new LogRecord { Timestamp = end.AddSeconds(-i % 300), Service = "api",
                Severity = i % 10 == 0 ? "ERROR" : "INFO", Body = "sample event " + i })).ToList();
        await harness.Writer.FlushAsync(records, default);
        var gate = new AlertExecutionGate(new SystemOptions(), TimeProvider.System);
        async Task<(double P95, double Ingest)> Measure(bool alerts)
        {
            var latency = new List<double>();
            var ingestMs = 0d;
            for (var i = 0; i < 30; i++)
            {
                var evaluation = alerts
                    ? gate.ExecuteAsync(harness.Executor, new AlertCountQuery("logs", "severity = $p0",
                        new Dictionary<string, object?> { ["p0"] = "ERROR" }, end.AddMinutes(-5), end, 2), default)
                    : Task.FromResult(new AlertCountResult(0, false));
                var watch = Stopwatch.StartNew();
                await harness.Executor.ExecuteSearchAsync("SELECT count(*) FROM {{logs}} WHERE ts >= $start AND ts < $end AND body LIKE $p0",
                    end.AddMinutes(-5), end, 1, new Dictionary<string, object?> { ["p0"] = "%sample%" });
                latency.Add(watch.Elapsed.TotalMilliseconds);
                watch.Restart();
                await harness.Writer.FlushAsync(records.Take(100).ToList(), default);
                ingestMs += watch.Elapsed.TotalMilliseconds;
                await evaluation;
            }
            latency.Sort();
            return (latency[(int)Math.Ceiling(latency.Count * 0.95) - 1], 3000 / (ingestMs / 1000));
        }
        await Measure(false);
        var baseline = await Measure(false);
        var mixed = await Measure(true);
        output.WriteLine($"100000 initial rows, 30 searches + 3000 ingested rows per sample; DuckDB threads=2");
        output.WriteLine($"baseline search P95={baseline.P95:F2}ms ingest={baseline.Ingest:F0} rows/s");
        output.WriteLine($"with serial capped alerts search P95={mixed.P95:F2}ms ingest={mixed.Ingest:F0} rows/s");
        Assert.True(double.IsFinite(mixed.P95));
    }
}
