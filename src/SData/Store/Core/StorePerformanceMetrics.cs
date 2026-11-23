using System.Diagnostics;
using SData.Store;

namespace SData.Store.Core;

internal sealed class StorePerformanceMetrics : IStorePerformanceMetrics
{
    private readonly Histogram _writeLockWait = new("sdata_store_write_lock_wait_seconds");
    private readonly Histogram _queryLockWait = new("sdata_store_query_lock_wait_seconds");
    private readonly Histogram _alertQueryLockWait = new("sdata_store_alert_query_lock_wait_seconds");
    private readonly Histogram _sourceLockWait = new("sdata_store_source_lock_wait_seconds");
    private readonly Histogram _sourceSwitchLockHold = new("sdata_store_source_switch_lock_hold_seconds");
    private readonly Histogram _tableLockWait = new("sdata_store_table_lock_wait_seconds");
    private readonly Histogram _maintenanceLockWait = new("sdata_store_maintenance_lock_wait_seconds");
    private readonly Histogram _ingestAppend = new("sdata_ingest_append_seconds");
    private readonly Histogram _ingestCommit = new("sdata_ingest_commit_seconds");
    private readonly Histogram _ingestAppendRowLoop = new("sdata_ingest_append_rowloop_seconds");
    private readonly Histogram _ingestAppendClose = new("sdata_ingest_close_seconds");
    private readonly Histogram _rollupDuration = new("sdata_rollup_duration_seconds");
    private readonly Histogram _archiveDuration = new("sdata_archive_duration_seconds");
    private readonly Histogram _retentionDuration = new("sdata_retention_duration_seconds");
    private long _rollupRows;
    private long _archiveRows;
    private long _retentionTablesDropped;
    private long _retentionArchiveRecordsExpired;
    private long _retentionRollupRowsDeleted;
    private long _ingestFastRejected;
    private long _ingestAppendRows;

    public StorePerformanceSnapshot Snapshot => new()
    {
        Histograms =
        [
            _writeLockWait.Snapshot(),
            _queryLockWait.Snapshot(),
            _alertQueryLockWait.Snapshot(),
            _sourceLockWait.Snapshot(),
            _sourceSwitchLockHold.Snapshot(),
            _tableLockWait.Snapshot(),
            _maintenanceLockWait.Snapshot(),
            _ingestAppend.Snapshot(),
            _ingestCommit.Snapshot(),
            _ingestAppendRowLoop.Snapshot(),
            _ingestAppendClose.Snapshot(),
            _rollupDuration.Snapshot(),
            _archiveDuration.Snapshot(),
            _retentionDuration.Snapshot(),
        ],
        Counters =
        [
            new() { Name = "sdata_rollup_rows_total", Value = Interlocked.Read(ref _rollupRows) },
            new() { Name = "sdata_archive_rows_total", Value = Interlocked.Read(ref _archiveRows) },
            new() { Name = "sdata_retention_tables_dropped_total", Value = Interlocked.Read(ref _retentionTablesDropped) },
            new() { Name = "sdata_retention_archive_records_expired_total", Value = Interlocked.Read(ref _retentionArchiveRecordsExpired) },
            new() { Name = "sdata_retention_rollup_rows_deleted_total", Value = Interlocked.Read(ref _retentionRollupRowsDeleted) },
            new() { Name = "sdata_ingest_fast_rejected_total", Value = Interlocked.Read(ref _ingestFastRejected) },
            new() { Name = "sdata_ingest_append_rows_total", Value = Interlocked.Read(ref _ingestAppendRows) },
        ],
    };

    public void RecordWriteLockWait(long start) => _writeLockWait.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
    public void RecordQueryLockWait(long start) => _queryLockWait.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
    public void RecordAlertQueryLockWait(long start) => _alertQueryLockWait.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
    public void RecordSourceLockWait(long start) => _sourceLockWait.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
    public void RecordSourceSwitchLockHold(long start) => _sourceSwitchLockHold.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
    public void RecordTableLockWait(long start) => _tableLockWait.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
    public void RecordMaintenanceLockWait(long start) => _maintenanceLockWait.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
    public void RecordIngestAppend(long start) => _ingestAppend.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
    public void RecordIngestCommit(long start) => _ingestCommit.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
    public void RecordIngestAppendRowLoop(double seconds) => _ingestAppendRowLoop.Record(seconds);
    public void RecordIngestAppendClose(double seconds) => _ingestAppendClose.Record(seconds);
    public void RecordIngestAppendRows(long count) => Interlocked.Add(ref _ingestAppendRows, count);
    public void RecordIngestFastRejected(long count) => Interlocked.Add(ref _ingestFastRejected, count);

    public void RecordRollupDuration(long start) => _rollupDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
    public void RecordRollupRows(long rows) => Interlocked.Add(ref _rollupRows, rows);
    public void RecordArchiveDuration(long start) => _archiveDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
    public void RecordArchiveRows(long rows) => Interlocked.Add(ref _archiveRows, rows);
    public void RecordRetentionDuration(long start) => _retentionDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
    public void RecordRetentionTablesDropped(long count) => Interlocked.Add(ref _retentionTablesDropped, count);
    public void RecordRetentionArchiveRecordsExpired(long count) => Interlocked.Add(ref _retentionArchiveRecordsExpired, count);
    public void RecordRetentionRollupRowsDeleted(long count) => Interlocked.Add(ref _retentionRollupRowsDeleted, count);

    private sealed class Histogram(string name)
    {
        private static readonly double[] Bounds = [0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5];
        private readonly long[] _buckets = new long[Bounds.Length];
        private long _count;
        private long _sumNanoseconds;

        public void Record(double seconds)
        {
            Interlocked.Increment(ref _count);
            var index = Array.FindIndex(Bounds, bound => seconds <= bound);
            if (index >= 0)
            {
                Interlocked.Increment(ref _buckets[index]);
            }

            Interlocked.Add(ref _sumNanoseconds, (long)(seconds * 1_000_000_000));
        }

        public StoreHistogramSnapshot Snapshot()
        {
            var counts = new long[_buckets.Length + 1];
            long cumulative = 0;
            for (var index = 0; index < _buckets.Length; index++)
            {
                cumulative += Interlocked.Read(ref _buckets[index]);
                counts[index] = cumulative;
            }

            counts[^1] = Interlocked.Read(ref _count);
            return new StoreHistogramSnapshot
            {
                Name = name,
                UpperBoundsSeconds = [.. Bounds, double.PositiveInfinity],
                BucketCounts = counts,
                Count = Interlocked.Read(ref _count),
                SumSeconds = Interlocked.Read(ref _sumNanoseconds) / 1_000_000_000d,
            };
        }
    }
}
