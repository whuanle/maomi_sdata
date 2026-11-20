using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Infra.Services;
using SData.Settings;
using SData.Settings.Services;
using SData.Store;
using SData.Store.Core;
using SData.Store.Models;

namespace SData.Store.Tests;

/// <summary>
/// DuckDB 存储集成测试：连接管理 → 写入 → 查询 全链路（临时目录，跑完即弃）.
/// </summary>
[Collection("content-root-serial")]
    public partial class StoreIntegrationTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly SystemOptions _systemOptions;
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly IngestBuffer _buffer;
    private readonly IngestWriterService _writer;
    private readonly LogStore _logStore;
    private readonly TraceStore _traceStore;
    private readonly MetricStore _metricStore;
    private readonly TelemetryStore _telemetryStore;

    /// <summary>表解析器（统一查询管线，供分析执行器测试复用）.</summary>
    public readonly TableResolver Resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="StoreIntegrationTests"/> class.
    /// </summary>
    public StoreIntegrationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "sdata-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        AppConst.ContentRoot = _tempDirectory;

        _systemOptions = new SystemOptions();
        _systemOptions.DuckDb.Path = Path.Combine("store-test.duckdb");
        _systemOptions.DuckDb.Threads = 2;

        _connectionManager = new DuckDbConnectionManager(_systemOptions, NullLogger<DuckDbConnectionManager>.Instance);
        _connectionManager.InitializeAsync().GetAwaiter().GetResult();
        new DuckDbSchemaBootstrapper(_connectionManager, NullLogger<DuckDbSchemaBootstrapper>.Instance).ApplyAsync().GetAwaiter().GetResult();

        _buffer = new IngestBuffer(_systemOptions);
        _writer = new IngestWriterService(_buffer, _connectionManager, _systemOptions, new SData.Infra.Services.DefaultIdProvider(1), NullLogger<IngestWriterService>.Instance);

        // 统一查询管线依赖（内存台账镜像：本测试无台账行，冷层恒空，行为与旧版一致）
        var sqlitePath = Path.Combine(_tempDirectory, "ledger.db");
        var dbOptions = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={sqlitePath}").Options;
        var ledger = new DatabaseContext(dbOptions);
        ledger.Database.EnsureCreated();
        var scopeFactory = new SingleScopeFactory(ledger);
        var settings = new SystemSettingsProvider(_systemOptions, scopeFactory, new SData.Infra.Services.DefaultIdProvider(1), NullLogger<SystemSettingsProvider>.Instance);
        var objectStore = new CompositeObjectStore(new LocalObjectStore(), new S3ObjectStore(_systemOptions, NullLogger<S3ObjectStore>.Instance));
        var coldCache = new ColdCacheService(_systemOptions, objectStore, NullLogger<ColdCacheService>.Instance);
        var ledgerCache = new ColdLedgerCache(scopeFactory, NullLogger<ColdLedgerCache>.Instance);
        Resolver = new TableResolver(_connectionManager, _systemOptions, settings, ledgerCache, coldCache, NullLogger<TableResolver>.Instance);
        _logStore = new LogStore(_connectionManager, _systemOptions, Resolver);
        _traceStore = new TraceStore(_connectionManager, _systemOptions);
        _metricStore = new MetricStore(_connectionManager, _systemOptions);
        _telemetryStore = new TelemetryStore(_connectionManager, _systemOptions);
    }

    /// <summary>目录读取实际 schema，刷新反映建表与删除而不依赖写入缓存.</summary>
    [Fact]
    public async Task AnalyticsCatalog_ReflectsActualSchemaAndRemoval()
    {
        var catalog = new AnalyticsCatalog(_connectionManager);
        var before = await catalog.GetTablesAsync();
        Assert.DoesNotContain(before, x => x.Name == "iot_20261119");
        await _connectionManager.ExecuteWriteAsync("CREATE TABLE iot_20261119 (ts TIMESTAMP NOT NULL, custom_value DOUBLE)");
        var table = Assert.Single((await catalog.GetTablesAsync()).Where(x => x.Name == "iot_20261119"));
        Assert.Equal("main", table.Schema);
        Assert.Equal("iot", table.Domain);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero), table.Day);
        Assert.Equal(new[] { "ts", "custom_value" }, table.Columns.Select(x => x.Name));
        Assert.False(table.Columns[0].Nullable);
        Assert.Equal("DOUBLE", table.Columns[1].Type);
        Assert.True(table.Columns[1].Nullable);
        await _connectionManager.ExecuteWriteAsync("DROP TABLE iot_20261119");
        Assert.DoesNotContain(await catalog.GetTablesAsync(), x => x.Name == "iot_20261119");
    }

    /// <summary>
    /// 写入一条日志并按服务过滤查询.
    /// </summary>
    [Fact]
    public async Task Log_WriteThenQuery_RoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(_buffer.TryEnqueueLog(new LogRecord
        {
            Timestamp = now,
            Service = "svc-a",
            Severity = "ERROR",
            Body = "boom happened",
            TraceId = "t1",
        }));

        await DrainAsync();

        var records = await _logStore.QueryAsync(new LogQuery
        {
            Start = now.AddMinutes(-5),
            End = now.AddMinutes(5),
            Service = "svc-a",
            Search = "boom",
            Limit = 10,
        });

        var record = Assert.Single(records);
        Assert.Equal("svc-a", record.Service);
        Assert.Equal("ERROR", record.Severity);
        Assert.Equal("boom happened", record.Body);
        Assert.Equal("t1", record.TraceId);
    }

    /// <summary>
    /// 写入 span 并按 traceId 取全链路.
    /// </summary>
    [Fact]
    public async Task Trace_WriteThenGetTrace_RoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        _buffer.TryEnqueueSpan(new SpanRecord
        {
            TraceId = "trace-1",
            SpanId = "s1",
            Timestamp = now,
            DurationUs = 1000,
            Service = "gateway",
            Name = "handle",
            Status = "ok",
        });
        _buffer.TryEnqueueSpan(new SpanRecord
        {
            TraceId = "trace-1",
            SpanId = "s2",
            ParentSpanId = "s1",
            Timestamp = now.AddMilliseconds(10),
            DurationUs = 500,
            Service = "modbus",
            Name = "poll",
            Status = "error",
        });

        await DrainAsync();

        var spans = await _traceStore.GetTraceAsync("trace-1");
        Assert.Equal(2, spans.Count);

        var summaries = await _traceStore.QueryAsync(new TraceQuery
        {
            Start = now.AddMinutes(-5),
            End = now.AddMinutes(5),
            HasError = true,
        });
        var summary = Assert.Single(summaries);
        Assert.Equal("trace-1", summary.TraceId);
        Assert.Equal(2, summary.SpanCount);
        Assert.Equal(1, summary.ErrorCount);
    }

    /// <summary>
    /// 写入指标点并聚合查询.
    /// </summary>
    [Fact]
    public async Task Metric_WriteThenAggregate_RoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            _buffer.TryEnqueueMetric(new MetricPointRecord
            {
                Name = "cpu_usage",
                Timestamp = now.AddSeconds(i),
                Value = 10 + i,
                LabelsJson = "{\"host\":\"a\"}",
                Kind = "gauge",
            });
        }

        await DrainAsync();

        var response = await _metricStore.QueryRangeAsync(new MetricSeriesQuery
        {
            Name = "cpu_usage",
            Start = now.AddMinutes(-5),
            End = now.AddMinutes(5),
            Aggregation = MetricAggregation.Avg,
            StepSeconds = 60,
        });

        var series = Assert.Single(response.SeriesList);
        Assert.True(series.Points.Count >= 1);
        Assert.Equal(12.0, series.Points.Average(x => x.Value!.Value), 1);
    }

    /// <summary>
    /// 写入遥测并查询最新值与聚合.
    /// </summary>
    [Fact]
    public async Task Telemetry_WriteThenQuery_RoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        _buffer.TryEnqueueTelemetry(new TelemetryRecord
        {
            DeviceKey = "dev-1",
            Signal = "temperature",
            Timestamp = now.AddSeconds(-30),
            NumValue = 20,
        });
        _buffer.TryEnqueueTelemetry(new TelemetryRecord
        {
            DeviceKey = "dev-1",
            Signal = "temperature",
            Timestamp = now,
            NumValue = 22,
        });

        await DrainAsync();

        var latest = await _telemetryStore.LatestAsync("dev-1");
        var temperature = Assert.Single(latest);
        Assert.Equal(22, temperature.NumValue);

        var response = await _telemetryStore.QueryAsync(new TelemetryQuery
        {
            DeviceKey = "dev-1",
            Start = now.AddMinutes(-5),
            End = now.AddMinutes(5),
            StepSeconds = 60,
        });

        var series = Assert.Single(response.SeriesList);
        Assert.Equal("temperature", series.Name);
        Assert.True(series.Points.Count >= 1);
        Assert.Equal(21.0, series.Points.Average(x => x.Value!.Value), 1);
    }

    /// <summary>
    /// 分析执行器：合法 SELECT 执行、非法写操作被拒绝.
    /// </summary>
    [Fact]
    public async Task Analytics_RejectsWrite_AllowsSelect()
    {
        var executor = new AnalyticsExecutor(_connectionManager, _systemOptions, Resolver, NullLogger<AnalyticsExecutor>.Instance);

        var result = await executor.ExecuteAsync("SELECT 1 AS one", 10);
        Assert.Equal("one", Assert.Single(result.Columns));

        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync("DELETE FROM logs_x", 10));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync("SELECT 1; SELECT 2", 10));
    }

    [Fact]
    public async Task AgentTools_QueryActualData_AndAnalyticsCancellationInterruptsNativeWork()
    {
        var now = DateTimeOffset.UtcNow;
        _buffer.TryEnqueueLog(new LogRecord { Timestamp = now, Service = "agent-svc", Severity = "ERROR", Body = "password=hidden", TraceId = "agent-trace" });
        _buffer.TryEnqueueSpan(new SpanRecord { Timestamp = now, Service = "agent-svc", TraceId = "agent-trace", SpanId = "span", Name = "operation", Status = "error", DurationUs = 1000 });
        _buffer.TryEnqueueMetric(new MetricPointRecord { Timestamp = now, Name = "agent.cpu", Value = 42 });
        await DrainAsync();
        var executor = new AnalyticsExecutor(_connectionManager, _systemOptions, Resolver, NullLogger<AnalyticsExecutor>.Instance);
        var queries = new SData.Agents.AgentQueries(executor, NullLogger<SData.Agents.AgentQueries>.Instance);
        foreach (var tool in new[] { "list_services", "search_logs", "search_traces", "get_trace", "list_metric_names", "query_metric_series", "summarize_errors" })
        {
            var result = await queries.ExecuteAsync(tool, new SData.Agents.AgentQuery { From = now.AddMinutes(-1), To = now.AddMinutes(1), TraceId = "agent-trace", Metric = "agent.cpu" }, default);
            var json = System.Text.Json.JsonSerializer.Serialize(result);
            Assert.Contains("\"rows\":[[", json);
            Assert.DoesNotContain("password=hidden", json);
        }
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync("SELECT sum(sin(a.range + b.range)) FROM range(1000000) a CROSS JOIN range(1000000) b", cancellationToken: cancel.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single((await executor.ExecuteAsync("SELECT 42")).Rows);
    }

    private async Task DrainAsync()
    {
        var batch = new List<IngestItem>();
        while (_buffer.Reader.TryRead(out var item))
        {
            batch.Add(item);
        }

        Assert.NotEmpty(batch);
        await _writer.FlushAsync(batch, CancellationToken.None);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _connectionManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 文件被 DuckDB 占用时忽略
        }
    }

    /// <summary>单例 scope 工厂（台账上下文直供，与 ArchiveTests 同构）.</summary>
    private sealed class SingleScopeFactory : IServiceScopeFactory
    {
        private readonly DatabaseContext _dbContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="SingleScopeFactory"/> class.
        /// </summary>
        /// <param name="dbContext">上下文.</param>
        public SingleScopeFactory(DatabaseContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <inheritdoc/>
        public IServiceScope CreateScope()
        {
            return new SingleScope(_dbContext);
        }

        private sealed class SingleScope : IServiceScope
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="SingleScope"/> class.
            /// </summary>
            /// <param name="dbContext">上下文.</param>
            public SingleScope(DatabaseContext dbContext)
            {
                ServiceProvider = new SingleProvider(dbContext);
            }

            /// <inheritdoc/>
            public IServiceProvider ServiceProvider { get; }

            /// <inheritdoc/>
            public void Dispose()
            {
                // 单例上下文由夹具统一释放
            }
        }

        private sealed class SingleProvider : IServiceProvider
        {
            private readonly DatabaseContext _dbContext;

            /// <summary>
            /// Initializes a new instance of the <see cref="SingleProvider"/> class.
            /// </summary>
            /// <param name="dbContext">上下文.</param>
            public SingleProvider(DatabaseContext dbContext)
            {
                _dbContext = dbContext;
            }

            /// <inheritdoc/>
            public object? GetService(Type serviceType)
            {
                return serviceType == typeof(DatabaseContext) ? _dbContext : null;
            }
        }
    }
}
