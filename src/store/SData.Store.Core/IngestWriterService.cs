using System.Data;
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
    private readonly SystemOptions _systemOptions;
    private readonly IIdProvider _idProvider;
    private readonly ILogger<IngestWriterService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestWriterService"/> class.
    /// </summary>
    /// <param name="buffer">缓冲.</param>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="systemOptions">系统配置.</param>
    /// <param name="idProvider">id 生成器（logs 行级雪花 id 落库统一赋值，各写入通道零改动）.</param>
    /// <param name="logger">日志.</param>
    public IngestWriterService(IngestBuffer buffer, DuckDbConnectionManager connectionManager, SystemOptions systemOptions, IIdProvider idProvider, ILogger<IngestWriterService> logger)
    {
        _buffer = buffer;
        _connectionManager = connectionManager;
        _systemOptions = systemOptions;
        _idProvider = idProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Ingest writer started (batch={BatchSize}, flush={FlushIntervalMs}ms)", _systemOptions.Ingest.BatchSize, _systemOptions.Ingest.FlushIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await _buffer.Reader.WaitToReadAsync(stoppingToken))
                {
                    break;
                }

                // 高吞吐时立即批量落库；低流量时短暂聚合更多条目，提升批量大小
                if (_buffer.Reader.Count < _systemOptions.Ingest.BatchSize / 2)
                {
                    await Task.Delay(_systemOptions.Ingest.FlushIntervalMs, stoppingToken);
                }

                var batch = new List<IngestItem>(_systemOptions.Ingest.BatchSize);
                while (batch.Count < _systemOptions.Ingest.BatchSize && _buffer.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

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
            foreach (var group in groups)
            {
                await AppendAsync(connection, group.Key.Kind, group.Key.Table, group.Value, cancellationToken);
            }

            transaction.Commit();
            foreach (var group in groups)
            {
                _buffer.MarkProcessed(group.Key.Kind, group.Value.Count);
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

    private async Task AppendAsync(DuckDBConnection connection, IngestKind kind, string table, List<object> records, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var logColumns = kind == IngestKind.Log
            ? await _connectionManager.GetLogColumnsAsync(connection, table, cancellationToken)
            : Array.Empty<string>();

        using var appender = connection.CreateAppender(table);

        foreach (var recordObject in records)
        {
            var row = appender.CreateRow();
            switch (kind)
            {
                case IngestKind.Log when recordObject is LogRecord log:
                    if (log.Id == 0)
                    {
                        log.Id = _idProvider.GetLongId();
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

        appender.Close();
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
