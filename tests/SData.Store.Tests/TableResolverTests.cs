using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Database.Entities;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Infra.Services;
using SData.Settings;
using SData.Settings.Services;
using SData.Store;
using SData.Store.Core;
using SData.Store.Models;
using SData.Store.QueryDsl;

namespace SData.Store.Tests;

/// <summary>
/// 表解析器与冷层查询测试（T510，契约 C2）：未配置零行为变化、S3 兜底通配验存 fail-open、本地+冷层混合区间查询.
/// </summary>
[Collection("content-root-serial")]
public class TableResolverTests
{
    /// <summary>未配置 S3：ColdParquetGlobs 恒为空（零行为变化）.</summary>
    [Fact]
    public async Task Resolve_Unconfigured_NoColdGlobs()
    {
        using var harness = new ResolverHarness();
        var today = DateTimeOffset.UtcNow;
        harness.Manager.EnsureDayTablesAsync(TimeTables.LogsPrefix, new[] { today }).GetAwaiter().GetResult();

        var resolved = await harness.Resolver.ResolveAsync(DataDomain.Logs, today.AddHours(-1), today.AddHours(1));

        Assert.NotEmpty(resolved.LocalTables);
        Assert.Empty(resolved.ColdParquetGlobs);
    }

    /// <summary>已配置 S3 但台账为空：兜底通配先验存——端点不可达 fail-open 跳过冷层分支，本地天表照常解析（空桶零命中走同一跳过逻辑）.</summary>
    [Fact]
    public async Task Resolve_ConfiguredUnreachable_FailOpenSkipsColdGlob()
    {
        using var harness = new ResolverHarness(options =>
        {
            options.S3.Endpoint = "http://127.0.0.1:1"; // 环回保留端口，连接立即失败（验存异常 → 跳过冷层）
            options.S3.Bucket = "sdata-cold";
            options.S3.Prefix = "sdata/archive";
        });
        var today = DateTimeOffset.UtcNow;
        harness.Manager.EnsureDayTablesAsync(TimeTables.LogsPrefix, new[] { today }).GetAwaiter().GetResult();

        var resolved = await harness.Resolver.ResolveAsync(DataDomain.Logs, today.AddHours(-1), today.AddHours(1));

        Assert.NotEmpty(resolved.LocalTables);
        Assert.Empty(resolved.ColdParquetGlobs);
    }

    /// <summary>验存异常进熔断窗口：期内后续查询不再触碰 S3（不重复告警、不逐查询付探测延迟），本地数据照常返回.</summary>
    [Fact]
    public async Task Resolve_ProbeBroken_CircuitSkipsRepeatProbesWithinWindow()
    {
        var logger = new RecordingLogger();
        using var harness = new ResolverHarness(options =>
        {
            options.S3.Endpoint = "http://127.0.0.1:1"; // 环回保留端口，连接立即失败
            options.S3.Bucket = "sdata-cold";
        }, logger);
        var today = DateTimeOffset.UtcNow;
        harness.Manager.EnsureDayTablesAsync(TimeTables.LogsPrefix, new[] { today }).GetAwaiter().GetResult();
        var from = today.AddHours(-1);
        var to = today.AddHours(1);

        var first = await harness.Resolver.ResolveAsync(DataDomain.Logs, from, to);
        Assert.Empty(first.ColdParquetGlobs);
        Assert.NotEmpty(logger.Warnings); // 首次探测失败有告警
        var warningsAfterFirst = logger.Warnings.Count;

        var second = await harness.Resolver.ResolveAsync(DataDomain.Logs, from, to);
        var third = await harness.Resolver.ResolveAsync(DataDomain.Logs, from, to);
        Assert.Empty(second.ColdParquetGlobs);
        Assert.NotEmpty(second.LocalTables);
        Assert.Empty(third.ColdParquetGlobs);
        Assert.Equal(warningsAfterFirst, logger.Warnings.Count); // 熔断窗口内第二、三次 Resolve 无新告警（未再触碰 S3）
    }

    /// <summary>记录 Warning 的测试 logger.</summary>
    private sealed class RecordingLogger : ILogger<TableResolver>
    {
        /// <summary>已记录的 Warning 消息.</summary>
        public List<string> Warnings { get; } = new();

        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Warning;
        }

        /// <inheritdoc/>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>编译含冷层通配：SQL 出现 read_parquet 分支（union_by_name 兼容历史对象缺列）且单引号转义.</summary>
    [Fact]
    public void Compile_WithColdGlob_ReadParquetBranch()
    {
        var model = new QueryDslModel { Range = new QueryRange { From = DateTimeOffset.UtcNow.AddHours(-1), To = DateTimeOffset.UtcNow.AddHours(1) } };
        var resolved = new ResolvedTables(
            [TimeTables.Name(TimeTables.LogsPrefix, DateTimeOffset.UtcNow)],
            ["s3://bkt/pfx/logs/**/*.parquet", "s3://bkt/pfx/it's/**/*.parquet"]);

        var compiled = QueryDslCompiler.Compile(model, DataDomain.Logs, resolved);

        Assert.Contains("read_parquet('s3://bkt/pfx/logs/**/*.parquet', union_by_name=true)", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("it''s", compiled.Sql, StringComparison.Ordinal);
    }

    /// <summary>验收：本地天表 + 冷层 parquet（本地文件模拟 S3 路径）混合区间查询，两路数据合并.</summary>
    [Fact]
    public async Task MixedQuery_LocalAndCold_Merges()
    {
        using var harness = new ResolverHarness();
        var now = DateTimeOffset.UtcNow;
        var batch = new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = now, Service = "local-svc", Severity = "INFO", Body = "from local" }),
        };
        await harness.Writer.FlushAsync(batch, CancellationToken.None);

        var coldPath = Path.Combine(harness.TempDirectory, "cold", "logs", "20200101.parquet");
        Directory.CreateDirectory(Path.GetDirectoryName(coldPath)!);
        var coldTs = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        harness.Manager.ExecuteWriteAsync(
            $"""COPY (SELECT 123 AS id, TIMESTAMP '{coldTs:yyyy-MM-dd HH:mm:ss}' AS ts, 'cold-svc' AS service, 'INFO' AS severity, 'from parquet' AS body, NULL AS trace_id, NULL AS span_id, NULL AS attributes, NULL AS resource, NULL AS source) TO '{coldPath.Replace("'", "''")}' (FORMAT PARQUET)""").GetAwaiter().GetResult();

        var model = new QueryDslModel
        {
            Range = new QueryRange { From = coldTs, To = now.AddMinutes(5) },
            Filters = [new QueryFilter { Field = "service", Op = "match", Value = System.Text.Json.JsonSerializer.SerializeToElement("svc") }],
            Limit = 10,
        };
        var localTable = harness.Manager.GetDayTables(TimeTables.LogsPrefix, coldTs, now.AddMinutes(5)).Single();
        var resolved = new ResolvedTables([localTable], [coldPath]);
        var compiled = QueryDslCompiler.Compile(model, DataDomain.Logs, resolved);

        var services = await harness.Manager.QueryAsync(compiled.Sql, compiled.Parameters, async reader =>
        {
            var list = new List<string>();
            while (await reader.ReadAsync())
            {
                list.Add(reader.GetString(reader.GetOrdinal("service")));
            }

            return list;
        }, CancellationToken.None);

        Assert.Equal(2, services.Count);
        Assert.Contains("local-svc", services);
        Assert.Contains("cold-svc", services);
    }

    /// <summary>台账驱动（T515）：窗内台账输出逐日本地绝对路径；rowCount=0 的空表行排除.</summary>
    [Fact]
    public async Task Resolve_LedgerRows_EmitPerDayColdFiles()
    {
        using var harness = new ResolverHarness();
        var d1 = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var d2 = new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var d3 = new DateTimeOffset(2020, 1, 3, 0, 0, 0, TimeSpan.Zero);
        harness.Ledger.ColdArchives.AddRange(
            new ColdArchiveEntity { Domain = "logs", Day = "20200101", ObjectKey = "data/cold/logs/20200101.parquet", RowCount = 3, ArchivedTime = DateTimeOffset.UtcNow },
            new ColdArchiveEntity { Domain = "logs", Day = "20200102", ObjectKey = "data/cold/logs/20200102.parquet", RowCount = 1, ArchivedTime = DateTimeOffset.UtcNow },
            new ColdArchiveEntity { Domain = "logs", Day = "20200103", ObjectKey = "data/cold/logs/20200103.parquet", RowCount = 0, ArchivedTime = DateTimeOffset.UtcNow });
        harness.Ledger.SaveChanges();

        var resolved = await harness.Resolver.ResolveAsync(DataDomain.Logs, d1, d3.AddDays(1));

        Assert.Empty(resolved.ColdParquetGlobs); // ColdFiles 非空时通配兜底互斥
        Assert.Equal(2, resolved.ColdFiles.Count); // 空表台账行（rowCount=0）不产生路径
        Assert.All(resolved.ColdFiles, file => Assert.StartsWith(harness.TempDirectory, file, StringComparison.Ordinal));
        Assert.Contains(resolved.ColdFiles, file => file.EndsWith("20200101.parquet", StringComparison.Ordinal));
        Assert.Contains(resolved.ColdFiles, file => file.EndsWith("20200102.parquet", StringComparison.Ordinal));
    }

    /// <summary>本地冷目录兜底（T515）：未配 S3、台账为空时，data/cold 存在归档对象则输出目录通配.</summary>
    [Fact]
    public async Task Resolve_NoS3_NoLedger_LocalColdDirFallback()
    {
        using var harness = new ResolverHarness();
        var coldDir = Path.Combine(harness.TempDirectory, "data", "cold", "logs");
        Directory.CreateDirectory(coldDir);
        File.WriteAllText(Path.Combine(coldDir, "20200101.parquet"), "dummy");

        var resolved = await harness.Resolver.ResolveAsync(DataDomain.Logs, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero));

        var glob = Assert.Single(resolved.ColdParquetGlobs);
        Assert.EndsWith("data/cold/logs/*[0-9].parquet", glob, StringComparison.Ordinal);
        Assert.Empty(resolved.ColdFiles);
    }

    /// <summary>关键词候选天裁剪（T518）：有索引镜像的天按词项命中裁剪，无镜像的天保守保留.</summary>
    [Fact]
    public async Task Resolve_Keyword_PrunesColdFilesByTermIndex()
    {
        using var harness = new ResolverHarness();
        var d1 = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var d2 = new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var d3 = new DateTimeOffset(2020, 1, 3, 0, 0, 0, TimeSpan.Zero);
        harness.Ledger.ColdArchives.AddRange(
            new ColdArchiveEntity { Domain = "logs", Day = "20200101", ObjectKey = "data/cold/logs/20200101.parquet", RowCount = 1, ArchivedTime = DateTimeOffset.UtcNow },
            new ColdArchiveEntity { Domain = "logs", Day = "20200102", ObjectKey = "data/cold/logs/20200102.parquet", RowCount = 1, ArchivedTime = DateTimeOffset.UtcNow });
        harness.Ledger.SaveChanges();

        // 仅 d1 有索引镜像：含 '超时'（bigram）与 'ord42'（token）
        var idx1 = ColdTermIndex.LocalMirrorPath("logs", "20200101");
        Directory.CreateDirectory(Path.GetDirectoryName(idx1)!);
        harness.Manager.ExecuteWriteAsync(
            $"""COPY (SELECT '超时' AS term UNION ALL SELECT 'ord42') TO '{idx1.Replace('\\', '/').Replace("'", "''")}' (FORMAT PARQUET)""").GetAwaiter().GetResult();
        Assert.True(File.Exists(idx1), "索引镜像应已写出");

        // 命中：d1（候选保留）+ d2（无镜像，保守保留）
        var hit = await harness.Resolver.ResolveAsync(DataDomain.Logs, d1, d3, "超时");
        Assert.Equal(2, hit.ColdFiles.Count);
        Assert.Contains(hit.ColdFiles, file => file.EndsWith("20200101.parquet", StringComparison.Ordinal));

        // 不命中：d1 被索引裁掉；仅剩无镜像的 d2（保守保留语义的另一半：有镜像才可裁）
        var miss = await harness.Resolver.ResolveAsync(DataDomain.Logs, d1, d3, "NoSuchTerm99");
        var missFile = Assert.Single(miss.ColdFiles);
        Assert.EndsWith("20200102.parquet", missFile, StringComparison.Ordinal);

        // 无关键词：全部保留
        var all = await harness.Resolver.ResolveAsync(DataDomain.Logs, d1, d3);
        Assert.Equal(2, all.ColdFiles.Count);
    }

    /// <summary>解析器测试夹具.</summary>
    private sealed class ResolverHarness : IDisposable
    {
        /// <summary>临时目录.</summary>
        public readonly string TempDirectory;

        /// <summary>连接管理器.</summary>
        public readonly DuckDbConnectionManager Manager;

        /// <summary>批量写入器.</summary>
        public readonly IngestWriterService Writer;

        /// <summary>表解析器.</summary>
        public readonly TableResolver Resolver;

        /// <summary>SQLite 台账上下文.</summary>
        public readonly DatabaseContext Ledger;

        /// <summary>运行时系统设置（覆盖保存用）.</summary>
        public readonly SystemSettingsProvider Settings;

        /// <summary>取消令牌.</summary>
        public readonly CancellationToken Token = CancellationToken.None;

        /// <summary>
        /// Initializes a new instance of the <see cref="ResolverHarness"/> class.
        /// </summary>
        /// <param name="configure">配置回调.</param>
        /// <param name="resolverLogger">解析器日志（缺省 NullLogger，可传入记录用 logger 断言告警）.</param>
        public ResolverHarness(Action<SystemOptions>? configure = null, ILogger<TableResolver>? resolverLogger = null)
        {
            TempDirectory = Path.Combine(Path.GetTempPath(), "sdata-tresolver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDirectory);
            AppConst.ContentRoot = TempDirectory;

            var options = new SystemOptions();
            options.DuckDb.Path = Path.Combine(TempDirectory, "tresolver-test.duckdb");
            options.DuckDb.Threads = 2;
            configure?.Invoke(options);

            Manager = new DuckDbConnectionManager(options, NullLogger<DuckDbConnectionManager>.Instance);
            Manager.InitializeAsync().GetAwaiter().GetResult();
            Writer = new IngestWriterService(new IngestBuffer(options), Manager, options, new DefaultIdProvider(1), NullLogger<IngestWriterService>.Instance);

            var sqlitePath = Path.Combine(TempDirectory, "ledger.db");
            var dbOptions = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={sqlitePath}").Options;
            Ledger = new DatabaseContext(dbOptions);
            Ledger.Database.EnsureCreated();
            var scopeFactory = new SingleScopeFactory(Ledger);
            Settings = new SystemSettingsProvider(options, scopeFactory, new DefaultIdProvider(1), NullLogger<SystemSettingsProvider>.Instance);
            var objectStore = new CompositeObjectStore(new LocalObjectStore(), new S3ObjectStore(options, NullLogger<S3ObjectStore>.Instance));
            var coldCache = new ColdCacheService(options, objectStore, NullLogger<ColdCacheService>.Instance);
            var ledgerCache = new ColdLedgerCache(scopeFactory, NullLogger<ColdLedgerCache>.Instance);
            Resolver = new TableResolver(Manager, options, Settings, ledgerCache, coldCache, resolverLogger ?? NullLogger<TableResolver>.Instance);
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
