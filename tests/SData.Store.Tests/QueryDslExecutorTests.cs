using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using SData.Database;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Infra.Exceptions;
using SData.Settings;
using SData.Settings.Services;
using SData.Store;
using SData.Store.Core;
using SData.Store.Models;
using SData.Store.QueryDsl;

namespace SData.Store.Tests;

/// <summary>
/// 统一查询 DSL 执行器测试（T311）：MaxQueryDays 约束、空表短路、真实 DuckDB 端到端执行.
/// </summary>
[Collection("content-root-serial")]
public class QueryDslExecutorTests
{
    private static JsonElement El<T>(T value) => JsonSerializer.SerializeToElement(value);

    /// <summary>冷层端到端（统一管线）：台账镜像分片经 QueryDsl 可查，含冷层放宽跨度（ColdMaxQueryDays）.</summary>
    [Fact]
    public async Task ExecuteAsync_ColdLedgerRows_Returned()
    {
        using var harness = new ExecutorHarness();
        var coldDay = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var coldPath = Path.Combine(harness.TempDirectory, "data", "cold", "logs", "20200101.parquet").Replace('\\', '/');
        Directory.CreateDirectory(Path.GetDirectoryName(coldPath)!);
        harness.Manager.ExecuteWriteAsync(
            $"""COPY (SELECT 1 AS id, TIMESTAMP '2020-01-01 08:00:00' AS ts, 'cold-svc' AS service, 'ERROR' AS severity, '支付网关连接超时' AS body, NULL AS trace_id, NULL AS span_id, NULL AS attributes, NULL AS resource, NULL AS source) TO '{coldPath}' (FORMAT PARQUET)""").GetAwaiter().GetResult();
        harness.Ledger.ColdArchives.Add(new SData.Database.Entities.ColdArchiveEntity { Domain = "logs", Day = "20200101", ObjectKey = "data/cold/logs/20200101.parquet", RowCount = 1, ArchivedTime = DateTimeOffset.UtcNow });
        await harness.Ledger.SaveChangesAsync(harness.Token);

        // 冷层（170+ 天前）参与 → 跨度放宽至 ColdMaxQueryDays；台账镜像分片可查
        var model = new QueryDslModel
        {
            Range = new QueryRange { From = coldDay, To = coldDay.AddDays(100) },
            Filters = [new QueryFilter { Field = "service", Op = "eq", Value = JsonSerializer.SerializeToElement("cold-svc") }],
            Limit = 10,
        };
        var response = await harness.Executor.ExecuteAsync(model, DataDomain.Logs, harness.Token);
        var row = Assert.Single(response.Rows);
        Assert.Equal("cold-svc", row["service"]);
    }

    /// <summary>MaxQueryDays 超限拒绝（400）.</summary>
    [Fact]
    public async Task ExecuteAsync_RejectsSpanBeyondMaxQueryDays()
    {
        using var harness = new ExecutorHarness(options => options.DuckDb.MaxQueryDays = 7);
        var model = new QueryDslModel
        {
            Range = new QueryRange { From = DateTimeOffset.UtcNow.AddDays(-30), To = DateTimeOffset.UtcNow },
        };

        var exception = await Assert.ThrowsAsync<BusinessException>(() => harness.Executor.ExecuteAsync(model, DataDomain.Logs, harness.Token));
        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("MaxQueryDays", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>区间内无天表：短路返回空响应（列名为域物理列）.</summary>
    [Fact]
    public async Task ExecuteAsync_EmptyRange_ReturnsEmptyResponse()
    {
        using var harness = new ExecutorHarness();
        var model = new QueryDslModel
        {
            Range = new QueryRange { From = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero), To = new DateTimeOffset(1980, 1, 2, 0, 0, 0, TimeSpan.Zero) },
        };

        var response = await harness.Executor.ExecuteAsync(model, DataDomain.Logs, harness.Token);

        Assert.Equal(0, response.TablesScanned);
        Assert.Empty(response.Rows);
        Assert.Contains("service", response.Columns);
        Assert.Contains("source", response.Columns);
    }

    /// <summary>logs 原始行端到端：落库 → DSL 查询 → 列式行返回.</summary>
    [Fact]
    public async Task ExecuteAsync_LogsRaw_ReturnsRows()
    {
        using var harness = new ExecutorHarness();
        var now = DateTimeOffset.UtcNow;
        var batch = new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now, Service = "web", Severity = "INFO", Body = "hello", Source = "logs-x" }),
            new(IngestKind.Log, new LogRecord { Timestamp = now, Service = "db", Severity = "WARN", Body = "slow" }),
        };
        await harness.Writer.FlushAsync(batch, harness.Token);

        var model = new QueryDslModel
        {
            Range = new QueryRange { From = now.AddMinutes(-5), To = now.AddMinutes(5) },
            Filters = [new QueryFilter { Field = "service", Op = "eq", Value = El("web") }],
            Sort = [new QuerySort { Field = "ts", Desc = true }],
            Limit = 10,
        };

        var response = await harness.Executor.ExecuteAsync(model, DataDomain.Logs, harness.Token);

        Assert.Equal(1, response.TablesScanned);
        var row = Assert.Single(response.Rows);
        Assert.Equal("web", row["service"]);
        Assert.Equal("hello", row["body"]);
        Assert.Equal("logs-x", row["source"]);
        Assert.IsType<DateTimeOffset>(row["ts"]);
    }

    /// <summary>metrics 聚合端到端：bucket 分桶 + avg 别名列.</summary>
    [Fact]
    public async Task ExecuteAsync_MetricsBucket_ReturnsAggregateColumns()
    {
        using var harness = new ExecutorHarness();
        var now = DateTimeOffset.UtcNow;
        var batch = new List<IngestItem>
        {
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = now.AddMinutes(-1), Value = 4 }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = now.AddMinutes(-2), Value = 6 }),
        };
        await harness.Writer.FlushAsync(batch, harness.Token);

        var model = new QueryDslModel
        {
            Range = new QueryRange { From = now.AddMinutes(-5), To = now.AddMinutes(5) },
            Bucket = new QueryBucket { Step = "1h", Func = "avg", Field = "value" },
            Limit = 10,
        };

        var response = await harness.Executor.ExecuteAsync(model, DataDomain.Metrics, harness.Token);

        Assert.Equal(1, response.TablesScanned);
        Assert.Contains("bucket", response.Columns);
        Assert.Contains("avg", response.Columns);
        var row = Assert.Single(response.Rows);
        Assert.Equal(5.0, Convert.ToDouble(row["avg"], System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.IsType<DateTimeOffset>(row["bucket"]);
    }

    /// <summary>执行测试夹具：临时库 + 缓冲 + 写入器 + DSL 执行器.</summary>
    private sealed class ExecutorHarness : IDisposable
    {
        /// <summary>临时目录.</summary>
        public readonly string TempDirectory;

        /// <summary>连接管理器.</summary>
        public readonly DuckDbConnectionManager Manager;

        /// <summary>批量写入器.</summary>
        public readonly IngestWriterService Writer;

        /// <summary>DSL 执行器.</summary>
        public readonly QueryDslExecutorService Executor;

        /// <summary>SQLite 台账上下文.</summary>
        public readonly DatabaseContext Ledger;

        /// <summary>取消令牌.</summary>
        public readonly CancellationToken Token = CancellationToken.None;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExecutorHarness"/> class.
        /// </summary>
        /// <param name="configure">配置回调.</param>
        public ExecutorHarness(Action<SystemOptions>? configure = null)
        {
            TempDirectory = Path.Combine(Path.GetTempPath(), "sdata-dslexe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDirectory);
            AppConst.ContentRoot = TempDirectory;

            var options = new SystemOptions();
            options.DuckDb.Path = Path.Combine(TempDirectory, "dslexe-test.duckdb");
            options.DuckDb.Threads = 2;
            configure?.Invoke(options);

            Manager = new DuckDbConnectionManager(options, NullLogger<DuckDbConnectionManager>.Instance);
            Manager.InitializeAsync().GetAwaiter().GetResult();
            Writer = new IngestWriterService(new IngestBuffer(options), Manager, SystemSettingsProvider.FileOnly(options), new SData.Infra.Services.DefaultIdProvider(1), NullLogger<IngestWriterService>.Instance);

            var sqlitePath = Path.Combine(TempDirectory, "ledger.db");
            var dbOptions = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={sqlitePath}").Options;
            Ledger = new DatabaseContext(dbOptions);
            Ledger.Database.EnsureCreated();
            var scopeFactory = new SingleScopeFactory(Ledger);
            var settings = new SystemSettingsProvider(options, scopeFactory, new SData.Infra.Services.DefaultIdProvider(1), NullLogger<SystemSettingsProvider>.Instance);
            var objectStore = new CompositeObjectStore(new LocalObjectStore(), new S3ObjectStore(options, NullLogger<S3ObjectStore>.Instance));
            var coldCache = new ColdCacheService(settings, objectStore, NullLogger<ColdCacheService>.Instance);
            var ledgerCache = new ColdLedgerCache(scopeFactory, NullLogger<ColdLedgerCache>.Instance);
            Executor = new QueryDslExecutorService(Manager, settings, new TableResolver(Manager, options, settings, ledgerCache, coldCache, NullLogger<TableResolver>.Instance));
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不阻塞测试
            }
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
