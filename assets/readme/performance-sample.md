# Local storage-layer performance sample

Historical local test output retained for the README; not a new benchmark run.

- Source: `.artifacts/alerts-performance-detail.log` (local verification artifact).
- Test: [`AlertPerformance_MixedSearchAndIngest_Sample`](../../tests/SData.Store.Tests/AlertPerformanceTests.cs).
- Environment recorded: Windows x64, .NET 10.0.11, DuckDB query threads = 2, Debug build.
- CPU, disk model, cache state, and run timestamp: not recorded.
- Data: 100,000 initial log records; each sample performs 30 searches and writes 3,000 records in batches of 100, after a warm-up sample.
- Search: count records matching a body substring in a five-minute time window.
- Write rate: 3,000 records divided by cumulative `Writer.FlushAsync` duration. It excludes HTTP, queueing, search time, and total mixed-workload duration.
- Alert workload: one serial capped alert evaluation per iteration; not a concurrent-user or saturation test.

Recorded output:

```text
100000 initial rows, 30 searches + 3000 ingested rows per sample; DuckDB threads=2
baseline search P95=2.48ms ingest=32509 rows/s
with serial capped alerts search P95=2.62ms ingest=31464 rows/s
Test run successful. Total tests: 1. Passed: 1.
```

These results are a short storage-component sample, not sustained end-to-end ingestion throughput, HTTP concurrency capacity, or a maximum performance claim. They are not suitable for cross-machine comparisons.
