using System.Data;
using System.Diagnostics;
using DuckDB.NET.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SData.Infra.Configuration;
using SData.Infra.Services;
using SData.Store.Models;

namespace SData.Store;

/// <summary>
/// 批量落库后台服务：从缓冲批量读取，按（类型，天）分组后用 Appender 写入 DuckDB.
/// </summary>
public class IngestWriterService : BackgroundService
{
    private readonly IngestBuffer _buffer;
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly ITuningState _tuning;
    private readonly ResourceInterner _interner;
    private readonly IIdProvider _idProvider;
    private readonly ILogger<IngestWriterService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestWriterService"/> class.
    /// </summary>
    /// <param name="buffer">缓冲.</param>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="tuning">自动调参状态（批量参数每轮读取，随扩缩调整）.</param>
    /// <param name="interner">resource 字典编码器（logs/traces 写入去重）.</param>
    /// <param name="idProvider">id 生成器（logs 行级雪花 id 落库统一赋值，各写入通道零改动）.</param>
    /// <param name="logger">日志.</param>
    public IngestWriterService(IngestBuffer buffer, DuckDbConnectionManager connectionManager, ITuningState tuning, ResourceInterner interner, IIdProvider idProvider, ILogger<IngestWriterService> logger)
    {
        _buffer = buffer;
        _connectionManager = connectionManager;
        _tuning = tuning;
        _interner = interner;
        _idProvider = idProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Ingest writer started (batch={BatchSize}, flush={FlushIntervalMs}ms)", _tuning.BatchSize, _tuning.FlushIntervalMs);

        var batch = new List<IngestItem>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await _buffer.Reader.WaitToReadAsync(stoppingToken))
                {
                    break;
                }

                // 批量参数每轮读取，AdaptiveTuningService 调整后下一批即生效
                var batchSize = _tuning.BatchSize;
                // 高吞吐时立即批量落库；低流量时短暂聚合更多条目，提升批量大小
                if (_buffer.Reader.Count < batchSize / 2 && !_buffer.HasPersistWaiters)
                {
                    // 低水位凑批改为有界等待：凑够半批立即出发，等满 FlushIntervalMs 兜底。
                    // 固定睡满间隔会让高吞吐下批次欠满、循环空转（压测实测损失 ~10% 排水占空比）；
                    // 5ms 轮询粒度对低流量延迟无感（每批最多多等一个轮询步长）。
                    var waitedMs = 0;
                    while (_buffer.Reader.Count < batchSize / 2 && waitedMs < _tuning.FlushIntervalMs)
                    {
                        var step = Math.Min(5, _tuning.FlushIntervalMs - waitedMs);
                        await Task.Delay(step, stoppingToken);
                        waitedMs += step;
                    }
                }

                batch.Clear();
                var counted = 0;
                while (batch.Count < batchSize && _buffer.Reader.TryRead(out var item))
                {
                    if (item.Counted)
                    {
                        counted++;
                    }

                    batch.Add(item);
                }

                // 已读出条目即刻释放条数闸门（内存占用仍由字节预算在 MarkProcessed 时释放）
                _buffer.ReleaseSlots(counted);

                if (batch.Count > 0)
                {
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        try
                        {
                            await FlushAsync(batch, stoppingToken);
                            _buffer.WriterRecovered();
                            foreach (var item in batch)
                            {
                                item.Persisted?.TrySetResult();
                            }

                            break;
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            _buffer.WriterFailed(exception);
                            _connectionManager.LogResourceFailure(exception, "ingest");
                            _logger.LogError(exception, "Ingest writer retains failed batch for retry");
                            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Ingest writer flush failed");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        _logger.LogInformation("Ingest writer stopped");
    }

    /// <summary>
    /// 批量落库（测试与后台循环共用）.
    /// </summary>
    /// <param name="batch">批次.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Task.</returns>
    internal async Task FlushAsync(List<IngestItem> batch, CancellationToken cancellationToken)
    {
        // 按（类型，天）分组；同一类型同一天复用表名，避免每条记录重复拼接字符串
        var groups = new Dictionary<(IngestKind Kind, string Table), List<object>>();
        var tableNames = new Dictionary<(IngestKind Kind, DateOnly Day), string>();
        foreach (var item in batch)
        {
            var time = GetTimestamp(item.Kind, item.Record);
            var day = DateOnly.FromDateTime(time.UtcDateTime);
            if (!tableNames.TryGetValue((item.Kind, day), out var table))
            {
                table = TimeTables.Name(Prefix(item.Kind), time);
                tableNames[(item.Kind, day)] = table;
            }
            var key = (item.Kind, table);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<object>();
                groups[key] = list;
            }

            list.Add(item.Record);
        }

        var leases = new List<IDisposable>();
        try
        {
            foreach (var table in groups.Keys.Select(k => k.Table).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                leases.Add(await _connectionManager.LockTableAsync(table, cancellationToken));
            }

            foreach (var kind in groups.Keys.Select(k => k.Kind).Distinct())
            {
                var prefix = Prefix(kind);
                var kindDays = groups.Keys.Where(k => k.Kind == kind).Select(k => TimeTables.ParseDay(k.Table)!.Value);
                await _connectionManager.EnsureDayTablesAsync(prefix, kindDays, cancellationToken);
            }

            using var writeLease = await _connectionManager.LockWriteAsync(cancellationToken);
            using var connection = _connectionManager.CloneConnection();
            using var transaction = connection.BeginTransaction();
            var appendStarted = Stopwatch.GetTimestamp();
            try
            {
                foreach (var group in groups)
                {
                    await AppendAsync(connection, group.Key.Kind, group.Key.Table, group.Value, cancellationToken);
                }
            }
            finally
            {
                _connectionManager.RecordIngestAppend(appendStarted);
            }

            var commitStarted = Stopwatch.GetTimestamp();
            try
            {
                transaction.Commit();
            }
            finally
            {
                _connectionManager.RecordIngestCommit(commitStarted);
            }
            foreach (var group in groups)
            {
                _buffer.MarkProcessed(group.Key.Kind, group.Value);
            }
        }
        finally
        {
            foreach (var lease in leases.AsEnumerable().Reverse())
            {
                lease.Dispose();
            }
        }
    }

    /// <summary>logs 数据表规范列序：旧版物理宽表（resource 原文）与视图门面窄表（resource_id 字典编码）；一致时走免 switch 的固定列序快路径.</summary>
    private static readonly string[] WideLogColumns = DuckDbConnectionManager.WideLogColumns;

    private static readonly string[] NarrowLogColumns = DuckDbConnectionManager.NarrowLogColumns;

    private async Task AppendAsync(DuckDBConnection connection, IngestKind kind, string table, List<object> records, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var day = TimeTables.ParseDay(table)!.Value;
        var isViewDay = _connectionManager.IsViewDay(table);

        // 视图门面天：数据写伴生窄表；resource 先 intern 去重，字典新条目先落数字典表（同一事务内先 res 后 data，引用完整）
        string? dataTable = table;
        string? resTable = null;
        long?[]? resourceIds = null;
        List<(long Id, string Json)>? resEntries = null;
        switch (kind)
        {
            case IngestKind.Log when isViewDay:
                dataTable = TimeTables.DataTableName(TimeTables.LogsPrefix, day);
                resTable = TimeTables.ResTableName(TimeTables.LogsPrefix, day);
                (resourceIds, resEntries) = InternResources<LogRecord>(day, records, static record => record.ResourceJson);
                break;
            case IngestKind.Trace when isViewDay:
                dataTable = TimeTables.DataTableName(TimeTables.TracesPrefix, day);
                resTable = TimeTables.ResTableName(TimeTables.TracesPrefix, day);
                (resourceIds, resEntries) = InternResources<SpanRecord>(day, records, static record => record.ResourceJson);
                break;
        }

        var logColumns = kind == IngestKind.Log
            ? await _connectionManager.GetLogColumnsAsync(connection, dataTable!, cancellationToken)
            : Array.Empty<string>();
        var logLayout = IsCanonicalLogColumns(logColumns);

        if (resEntries is { Count: > 0 })
        {
            using (var resAppender = connection.CreateAppender(resTable!))
            {
                foreach (var (id, json) in resEntries)
                {
                    var resRow = resAppender.CreateRow();
                    resRow.AppendValue(id);
                    resRow.AppendValue(json);
                    resRow.EndRow();
                }

                resAppender.Close();
            }
        }

        using var appender = connection.CreateAppender(dataTable!);
        var rowLoopStarted = Stopwatch.GetTimestamp();

        for (var index = 0; index < records.Count; index++)
        {
            var recordObject = records[index];
            var row = appender.CreateRow();
            switch (kind)
            {
                case IngestKind.Log when recordObject is LogRecord log:
                    if (log.Id == 0)
                    {
                        log.Id = _idProvider.GetLongId();
                    }

                    if (logLayout == LogLayout.Narrow)
                    {
                        row.AppendValue(log.Id).AppendValue(Normalize(log.Timestamp)).AppendValue(log.Service)
                            .AppendValue(log.Severity).AppendValue(log.Body ?? string.Empty).AppendValue(log.TraceId)
                            .AppendValue(log.SpanId).AppendValue(log.AttributesJson);
                        AppendResourceId(row, resourceIds![index]);
                        row.AppendValue(log.Source);
                        break;
                    }

                    if (logLayout == LogLayout.Wide)
                    {
                        row.AppendValue(log.Id).AppendValue(Normalize(log.Timestamp)).AppendValue(log.Service)
                            .AppendValue(log.Severity).AppendValue(log.Body ?? string.Empty).AppendValue(log.TraceId)
                            .AppendValue(log.SpanId).AppendValue(log.AttributesJson).AppendValue(log.ResourceJson)
                            .AppendValue(log.Source);
                        break;
                    }

                    foreach (var column in logColumns)
                    {
                        switch (column)
                        {
                            case "id": row.AppendValue(log.Id); break;
                            case "ts": row.AppendValue(Normalize(log.Timestamp)); break;
                            case "service": row.AppendValue(log.Service); break;
                            case "severity": row.AppendValue(log.Severity); break;
                            case "body": row.AppendValue(log.Body ?? string.Empty); break;
                            case "trace_id": row.AppendValue(log.TraceId); break;
                            case "span_id": row.AppendValue(log.SpanId); break;
                            case "attributes": row.AppendValue(log.AttributesJson); break;
                            case "resource": row.AppendValue(log.ResourceJson); break;
                            case "resource_id": AppendResourceId(row, resourceIds![index]); break;
                            case "source": row.AppendValue(log.Source); break;
                            default: throw new InvalidOperationException("不支持的日志列：" + column);
                        }
                    }
                    break;
                case IngestKind.MqConsumption when recordObject is MqConsumptionRecord sample:
                    row.AppendValue(Normalize(sample.Timestamp)).AppendValue(sample.InstanceId).AppendValue(sample.SessionId)
                        .AppendValue(sample.Rule).AppendValue(sample.MatchMode).AppendValue(sample.State)
                        .AppendValue(sample.ReceivedMessages).AppendValue(sample.ReceivedBytes).AppendValue(sample.PersistedRecords)
                        .AppendValue(sample.ParseFailures).AppendValue(sample.SkippedMessages).AppendValue(sample.ConfirmedMessages)
                        .AppendValue(sample.ConfirmationFailures).AppendValue(sample.SamplingGaps).AppendValue(sample.DiagnosticsJson);
                    break;
                case IngestKind.Trace when recordObject is SpanRecord span:
                    if (isViewDay)
                    {
                        row.AppendValue(span.TraceId).AppendValue(span.SpanId).AppendValue(span.ParentSpanId)
                            .AppendValue(Normalize(span.Timestamp)).AppendValue(span.DurationUs).AppendValue(span.Service)
                            .AppendValue(span.Name).AppendValue(span.Kind).AppendValue(span.Status)
                            .AppendValue(span.AttributesJson).AppendValue(span.EventsJson);
                        AppendResourceId(row, resourceIds![index]);
                        break;
                    }

                    row.AppendValue(span.TraceId).AppendValue(span.SpanId).AppendValue(span.ParentSpanId)
                        .AppendValue(Normalize(span.Timestamp)).AppendValue(span.DurationUs).AppendValue(span.Service)
                        .AppendValue(span.Name).AppendValue(span.Kind).AppendValue(span.Status)
                        .AppendValue(span.AttributesJson).AppendValue(span.EventsJson).AppendValue(span.ResourceJson);
                    break;
                case IngestKind.Metric when recordObject is MetricPointRecord metric:
                    row.AppendValue(metric.Name).AppendValue(Normalize(metric.Timestamp)).AppendValue(metric.Value)
                        .AppendValue(metric.LabelsJson).AppendValue(metric.Kind);
                    break;
                case IngestKind.Telemetry when recordObject is TelemetryRecord telemetry:
                    row.AppendValue(telemetry.DeviceKey).AppendValue(telemetry.Signal).AppendValue(Normalize(telemetry.Timestamp))
                        .AppendValue(telemetry.NumValue).AppendValue(telemetry.StrValue).AppendValue(telemetry.Quality);
                    break;
                case IngestKind.RumEvent when recordObject is RumEventRecord rumEvent:
                    row.AppendValue(Normalize(rumEvent.Timestamp)).AppendValue(rumEvent.ProjectKey).AppendValue(rumEvent.SessionId)
                        .AppendValue(rumEvent.EventType).AppendValue(rumEvent.EventName).AppendValue(rumEvent.PagePath)
                        .AppendValue(rumEvent.Value).AppendValue(rumEvent.PayloadJson);
                    break;
                case IngestKind.RumSession when recordObject is RumSessionRecord rumSession:
                    row.AppendValue(Normalize(rumSession.Timestamp)).AppendValue(rumSession.ProjectKey).AppendValue(rumSession.SessionId)
                        .AppendValue(rumSession.UserAgent).AppendValue(rumSession.Domain);
                    break;
                case IngestKind.RumReplay when recordObject is RumReplayRecord rumReplay:
                    row.AppendValue(Normalize(rumReplay.Timestamp)).AppendValue(rumReplay.ProjectKey).AppendValue(rumReplay.SessionId)
                        .AppendValue(rumReplay.Seq).AppendValue(rumReplay.BlobBase64);
                    break;
            }

            row.EndRow();
        }

        var closeStarted = Stopwatch.GetTimestamp();
        _connectionManager.RecordIngestAppendRows(records.Count);
        _connectionManager.RecordIngestAppendRowLoop(Stopwatch.GetElapsedTime(rowLoopStarted).TotalSeconds);
        appender.Close();
        _connectionManager.RecordIngestAppendClose(Stopwatch.GetElapsedTime(closeStarted).TotalSeconds);
    }

    /// <summary>logs 数据表列布局（宽 = 旧版物理表存 resource 原文；窄 = 视图门面数据表存 resource_id）.</summary>
    private enum LogLayout
    {
        Other,
        Wide,
        Narrow,
    }

    /// <summary>列集与规范序完全一致时返回布局（缓存由 GetLogColumnsAsync 承担，这里仅做线性比对/批）.</summary>
    private static LogLayout IsCanonicalLogColumns(IReadOnlyList<string> logColumns)
    {
        if (Matches(logColumns, NarrowLogColumns))
        {
            return LogLayout.Narrow;
        }

        return Matches(logColumns, WideLogColumns) ? LogLayout.Wide : LogLayout.Other;
    }

    private static bool Matches(IReadOnlyList<string> columns, string[] expected)
    {
        if (columns.Count != expected.Length)
        {
            return false;
        }

        for (var i = 0; i < columns.Count; i++)
        {
            if (!string.Equals(columns[i], expected[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 批量 intern resource 原文（视图门面天）；返回每条记录的 resource_id 与需落字典表的新条目.
    /// </summary>
    /// <typeparam name="T">记录类型（LogRecord/SpanRecord）.</typeparam>
    /// <param name="day">数据天.</param>
    /// <param name="records">记录列表.</param>
    /// <param name="resourceSelector">resource 原文选择器.</param>
    /// <returns>(每条 resource_id, 新字典条目).</returns>
    private (long?[] Ids, List<(long Id, string Json)> Entries) InternResources<T>(DateTimeOffset day, List<object> records, Func<T, string?> resourceSelector)
        where T : class
    {
        var ids = new long?[records.Count];
        var entries = new List<(long Id, string Json)>();
        for (var i = 0; i < records.Count; i++)
        {
            var result = _interner.Intern(day, resourceSelector((T)records[i]));
            ids[i] = result.Id;
            if (result.IsNew)
            {
                entries.Add((result.Id!.Value, result.Json!));
            }
        }

        return (ids, entries);
    }

    /// <summary>resource_id 可空 BIGINT 追加（null 对应旧宽表的 NULL resource）.</summary>
    private static void AppendResourceId(DuckDB.NET.Data.IDuckDBAppenderRow row, long? id)
    {
        if (id is { } value)
        {
            row.AppendValue(value);
        }
        else
        {
            row.AppendNullValue();
        }
    }

    private static string Prefix(IngestKind kind)
    {
        return kind switch
        {
            IngestKind.MqConsumption => TimeTables.MqConsumptionPrefix,
            IngestKind.Log => TimeTables.LogsPrefix,
            IngestKind.Trace => TimeTables.TracesPrefix,
            IngestKind.Metric => TimeTables.MetricsPrefix,
            IngestKind.RumEvent => TimeTables.RumEventPrefix,
            IngestKind.RumSession => TimeTables.RumSessionPrefix,
            IngestKind.RumReplay => TimeTables.RumReplayPrefix,
            _ => TimeTables.IotPrefix,
        };
    }

    private static DateTimeOffset GetTimestamp(IngestKind kind, object record)
    {
        return kind switch
        {
            IngestKind.MqConsumption => ((MqConsumptionRecord)record).Timestamp,
            IngestKind.Log => ((LogRecord)record).Timestamp,
            IngestKind.Trace => ((SpanRecord)record).Timestamp,
            IngestKind.Metric => ((MetricPointRecord)record).Timestamp,
            IngestKind.RumEvent => ((RumEventRecord)record).Timestamp,
            IngestKind.RumSession => ((RumSessionRecord)record).Timestamp,
            IngestKind.RumReplay => ((RumReplayRecord)record).Timestamp,
            _ => ((TelemetryRecord)record).Timestamp,
        };
    }

    private static DateTime Normalize(DateTimeOffset time)
    {
        return time.ToUniversalTime().UtcDateTime;
    }
}
