using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// 冷存档与三段保留测试（T512）：归档幂等/台账一致/三段 DROP 语义/旧形态兼容.
/// </summary>
[Collection("content-root-serial")]
    public partial class ArchiveTests
{
    /// <summary>
    /// 调服务前瞬时重设 ContentRoot（返回原夹具以便链式调用）.
    /// </summary>
    /// <param name="harness">夹具.</param>
    /// <returns>夹具.</returns>
    private static ArchiveHarness Pin(ArchiveHarness harness)
    {
        harness.PinContentRoot();
        return harness;
    }
    /// <summary>归档幂等：一轮归档后台账+对象+DROP 齐备，第二轮零归档.</summary>
    [Fact]
    public async Task Archive_Idempotent_LedgerAndDrop()
    {
        using var harness = new ArchiveHarness(options => options.Retention.Logs.HotDays = 7);
        var day = DateTimeOffset.UtcNow.Date.AddDays(-40);
        await harness.CreateDayTableWithRowsAsync(TimeTables.LogsPrefix, day, 2);

        var archived = await Pin(harness).Archive.RunOnceAsync(harness.Token);
        Assert.Equal(1, archived);
        Assert.Equal(2, harness.Manager.Snapshot.Counters.Single(item => item.Name == "sdata_archive_rows_total").Value);
        Assert.True(harness.Manager.Snapshot.Histograms.Single(item => item.Name == "sdata_archive_duration_seconds").Count > 0);

        var ledger = await harness.GetLedgerAsync("logs");
        var row = Assert.Single(ledger);
        Assert.Equal(TimeTables.Name(TimeTables.LogsPrefix, day)[5..], row.Day);
        Assert.Equal(2, row.RowCount);
        Assert.True(harness.ColdObjectExists("logs", row.Day));

        Assert.DoesNotContain(TimeTables.Name(TimeTables.LogsPrefix, day), harness.Manager.GetManagedTables(TimeTables.LogsPrefix));

        Assert.Equal(0, await Pin(harness).Archive.RunOnceAsync(harness.Token));
    }

    /// <summary>一天热存期归档昨天的完整天表，不归档当天数据.</summary>
    [Fact]
    public async Task Archive_OneDay_ArchivesYesterdayButNotToday()
    {
        using var harness = new ArchiveHarness(options => options.Retention.Logs.HotDays = 1);
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        await harness.CreateDayTableWithRowsAsync(TimeTables.LogsPrefix, today.AddDays(-1), 2);
        await harness.CreateDayTableWithRowsAsync(TimeTables.LogsPrefix, today, 1);

        Assert.Equal(1, await Pin(harness).Archive.RunOnceAsync(harness.Token));
        var row = Assert.Single(await harness.GetLedgerAsync("logs"));
        Assert.Equal(today.AddDays(-1).ToString("yyyyMMdd"), row.Day);
        Assert.Equal(2, row.RowCount);
        Assert.Contains(TimeTables.Name(TimeTables.LogsPrefix, today), harness.Manager.GetManagedTables(TimeTables.LogsPrefix));
        Assert.Equal(0, await Pin(harness).Archive.RunOnceAsync(harness.Token));
    }

    /// <summary>S3 URI 不经过本地文件路径操作（Windows 上亦不抛非法路径异常）.</summary>
    [Fact]
    public void Archive_S3Destination_SkipsLocalDirectoryPreparation()
    {
        ArchiveService.PrepareDestination("s3://sdata-cold/archive/logs/20261117.parquet");
    }

    /// <summary>冻结表的 COPY 与词项索引可在写门仍被占用时通过克隆连接执行，不阻塞其他写入门持有者.</summary>
    [Fact]
    public async Task Archive_CopyAndIndex_DoNotAcquireWriteGate()
    {
        using var harness = new ArchiveHarness(options => options.Retention.Logs.HotDays = 1);
        var day = DateTimeOffset.UtcNow.Date.AddDays(-2);
        await harness.CreateDayTableWithRowsAsync(TimeTables.LogsPrefix, day, 2);
        var snapshot = await harness.Manager.FreezeDayAsync(TimeTables.LogsPrefix, day, harness.Token);
        Assert.NotNull(snapshot);

        var parquetPath = Path.Combine(harness.TempDirectory, "maintenance-copy.parquet").Replace('\\', '/');
        var indexPath = Path.Combine(harness.TempDirectory, "maintenance-index.idx.parquet").Replace('\\', '/');
        using (await harness.Manager.LockWriteAsync(harness.Token))
        {
            await harness.Manager.ExecuteMaintenanceAsync(
                $"COPY (SELECT * FROM {snapshot} ORDER BY service) TO '{parquetPath.Replace("'", "''")}' (FORMAT PARQUET, COMPRESSION snappy)",
                cancellationToken: harness.Token);
            await harness.Manager.ExecuteMaintenanceAsync(
                ColdTermIndex.BuildIndexSql(snapshot!, indexPath, "snappy"),
                cancellationToken: harness.Token);
        }

        Assert.True(File.Exists(parquetPath));
        Assert.True(File.Exists(indexPath));
        Assert.Equal(2, await harness.Manager.CountAsync(snapshot!, harness.Token));
    }

    /// <summary>三段保留：归档即 DROP 热表；冷段到期（hot+cold）后清理冷对象与台账行，冷窗口内对象存活.</summary>
    [Fact]
    public async Task Retention_ThreeSegments_DropsOnlyArchivedExpired()
    {
        using var harness = new ArchiveHarness(options =>
        {
            options.Retention.Logs.HotDays = 7;
            options.Retention.Logs.ColdDays = 30;
        });

        var expiredDay = DateTimeOffset.UtcNow.Date.AddDays(-40);
        var warmDay = DateTimeOffset.UtcNow.Date.AddDays(-10);
        await harness.CreateDayTableWithRowsAsync(TimeTables.LogsPrefix, expiredDay, 1);
        await harness.CreateDayTableWithRowsAsync(TimeTables.LogsPrefix, warmDay, 1);

        // 热段→冷段：归档即 DROP 热表（数据转入冷层 parquet）
        await Pin(harness).Archive.RunOnceAsync(harness.Token);
        Assert.Empty(harness.Manager.GetManagedTables(TimeTables.LogsPrefix));
        Assert.Equal(2, (await harness.GetLedgerAsync("logs")).Count);
        Assert.True(harness.ColdObjectExists("logs", TimeTables.Name(TimeTables.LogsPrefix, expiredDay)[5..]));
        Assert.True(harness.ColdObjectExists("logs", TimeTables.Name(TimeTables.LogsPrefix, warmDay)[5..]));

        // 冷段→过期：超过 hot+cold 的对象与台账行清理，冷窗口内存活
        await Pin(harness).Retention.RunOnceAsync(harness.Token);
        var counters = harness.Manager.Snapshot.Counters.ToDictionary(item => item.Name, StringComparer.Ordinal);
        Assert.True(counters["sdata_retention_archive_records_expired_total"].Value > 0);
        Assert.True(harness.Manager.Snapshot.Histograms.Single(item => item.Name == "sdata_retention_duration_seconds").Count > 0);
        var ledger = await harness.GetLedgerAsync("logs");
        var row = Assert.Single(ledger);
        Assert.Equal(TimeTables.Name(TimeTables.LogsPrefix, warmDay)[5..], row.Day);
        Assert.True(harness.ColdObjectExists("logs", row.Day));
        Assert.False(harness.ColdObjectExists("logs", TimeTables.Name(TimeTables.LogsPrefix, expiredDay)[5..]));
    }

    /// <summary>冷对象删除期间不持源切换锁；删除失败时恢复查询可见性并由下轮重试.</summary>
    [Fact]
    public async Task Retention_ExpiredObjectDelete_DoesNotHoldSourceLockAndRetries()
    {
        var objects = new BlockingFailingObjectStore();
        using var harness = new ArchiveHarness(options =>
        {
            options.Retention.Logs.HotDays = 7;
            options.Retention.Logs.ColdDays = 30;
        }, objects);

        var expiredDay = DateTimeOffset.UtcNow.Date.AddDays(-40);
        await harness.CreateDayTableWithRowsAsync(TimeTables.LogsPrefix, expiredDay, 1);
        Assert.Equal(1, await Pin(harness).Archive.RunOnceAsync(harness.Token));

        var retention = Pin(harness).Retention.RunOnceAsync(harness.Token);
        await objects.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using (var reader = await harness.Manager.ReadSourcesAsync(harness.Token)
                   .WaitAsync(TimeSpan.FromSeconds(1)))
        {
            var hidden = await harness.Resolver.ResolveAsync(
                DataDomain.Logs, expiredDay, expiredDay.AddHours(12), cancellationToken: harness.Token);
            Assert.Empty(hidden.LocalTables);
            Assert.Empty(hidden.ColdFiles);
            Assert.Empty(hidden.ColdParquetGlobs);
        }

        objects.ReleaseDelete.TrySetResult();
        await retention;

        var ledger = Assert.Single(await harness.GetLedgerAsync("logs"));
        Assert.True(harness.ColdObjectExists("logs", ledger.Day));
        var restored = await harness.Resolver.ResolveAsync(
            DataDomain.Logs, expiredDay, expiredDay.AddHours(12), cancellationToken: harness.Token);
        Assert.Single(restored.ColdFiles);

        await Pin(harness).Retention.RunOnceAsync(harness.Token);
        Assert.Empty(await harness.GetLedgerAsync("logs"));
        Assert.False(harness.ColdObjectExists("logs", ledger.Day));
    }

    /// <summary>旧形态兼容：ColdDays=0 时超 hot 天数直接 DROP 且不产生台账（行为与旧版一致）.</summary>
    [Fact]
    public async Task LegacyMode_DropsDirectly_WithoutLedger()
    {
        using var harness = new ArchiveHarness(options =>
        {
            options.Retention.Logs.HotDays = 0;
            options.Retention.Logs.ColdDays = 0;
            options.DuckDb.Retention.Logs = 5;
        });

        var day = DateTimeOffset.UtcNow.Date.AddDays(-10);
        await harness.CreateDayTableWithRowsAsync(TimeTables.LogsPrefix, day, 1);

        Assert.Equal(0, await Pin(harness).Archive.RunOnceAsync(harness.Token));
        await Pin(harness).Retention.RunOnceAsync(harness.Token);

        Assert.True(harness.Manager.Snapshot.Counters.Single(item => item.Name == "sdata_retention_tables_dropped_total").Value > 0);
        Assert.DoesNotContain(TimeTables.Name(TimeTables.LogsPrefix, day), harness.Manager.GetManagedTables(TimeTables.LogsPrefix));
        Assert.Empty(await harness.GetLedgerAsync("logs"));
    }

    /// <summary>归档增强（行级 id/排序写/词项索引镜像）：行含写入器赋值的 id；对象按 service 升序；索引镜像含正文 token 与中文二元组.</summary>
    [Fact]
    public async Task Archive_SortedParquet_WithId_AndTermIndexMirror()
    {
        using var harness = new ArchiveHarness(options => options.Retention.Logs.HotDays = 7);
        var day = DateTimeOffset.UtcNow.Date.AddDays(-40);
        var ts = day.AddHours(8);
        var batch = new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = ts, Service = "zeta", Severity = "INFO", Body = "order done ORD42 ok" }),
            new(IngestKind.Log, new LogRecord { Timestamp = ts.AddMinutes(1), Service = "alpha", Severity = "ERROR", Body = "支付网关连接超时 timeout" }),
            new(IngestKind.Log, new LogRecord { Timestamp = ts.AddMinutes(2), Service = "mike", Severity = "WARN", Body = "redis 连接池告警" }),
        };
        await harness.Writer.FlushAsync(batch, harness.Token);

        Assert.Equal(1, await Pin(harness).Archive.RunOnceAsync(harness.Token));
        var row = Assert.Single(await harness.GetLedgerAsync("logs"));

        var parquetPath = Path.Combine(harness.TempDirectory, row.ObjectKey).Replace('\\', '/');
        var services = await QueryListAsync(harness, $"SELECT service FROM read_parquet('{parquetPath}')");
        Assert.Equal(new[] { "alpha", "mike", "zeta" }, services); // 归档按 service 排序（zonemap 裁剪前提）

        var idCount = await QueryScalarAsync(harness, $"SELECT count(*) FROM read_parquet('{parquetPath}') WHERE id IS NOT NULL");
        Assert.Equal(3, idCount); // 写入路径统一赋行级雪花 id

        var idxPath = Directory.GetFiles(Path.Combine(harness.TempDirectory, "data", "cold-idx", "logs"), "*.idx.parquet").Single();
        Assert.True(File.Exists(idxPath), "词项索引本地镜像应随归档生成");
        Assert.Equal(1, await QueryScalarAsync(harness, $"SELECT count(*) FROM read_parquet('{idxPath.Replace('\\', '/')}') WHERE term = '超时'"));
        Assert.Equal(1, await QueryScalarAsync(harness, $"SELECT count(*) FROM read_parquet('{idxPath.Replace('\\', '/')}') WHERE term LIKE '%ord42%'"));
    }

    /// <summary>小时分片粒度（ArchiveGranularity=hour）：一天归档为多个小时对象 + 台账多行 + 逐分片索引镜像，空分片跳过.</summary>
    [Fact]
    public async Task Archive_HourlyGranularity_ShardedObjectsAndLedger()
    {
        using var harness = new ArchiveHarness(options =>
        {
            options.Retention.Logs.HotDays = 7;
            options.S3.ArchiveGranularity = "hour";
        });
        var day = new DateTimeOffset(2020, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var batch = new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord { Timestamp = day.AddHours(8), Service = "zeta", Severity = "INFO", Body = "order ORD42 done" }),
            new(IngestKind.Log, new LogRecord { Timestamp = day.AddHours(8).AddMinutes(20), Service = "alpha", Severity = "INFO", Body = "order ORD43 done" }),
            new(IngestKind.Log, new LogRecord { Timestamp = day.AddHours(9).AddMinutes(30), Service = "mike", Severity = "ERROR", Body = "支付网关连接超时" }),
        };
        await harness.Writer.FlushAsync(batch, harness.Token);

        Assert.Equal(1, await Pin(harness).Archive.RunOnceAsync(harness.Token)); // 一张天表一轮归档

        var ledger = await harness.GetLedgerAsync("logs");
        Assert.Equal(2, ledger.Count); // 小时 08/09 非空；其余 22 个空分片跳过
        Assert.All(ledger, row => Assert.Equal("20201101", row.Day));
        Assert.Contains(ledger, row => row.ObjectKey.EndsWith("2020110108.parquet", StringComparison.Ordinal));
        Assert.Contains(ledger, row => row.ObjectKey.EndsWith("2020110109.parquet", StringComparison.Ordinal));
        Assert.Equal(3, ledger.Sum(row => row.RowCount));

        var shard08 = Path.Combine(harness.TempDirectory, ledger.Single(x => x.ObjectKey.EndsWith("2020110108.parquet", StringComparison.Ordinal)).ObjectKey).Replace('\\', '/');
        Assert.True(File.Exists(shard08));
        var services = await QueryListAsync(harness, $"SELECT service FROM read_parquet('{shard08}')");
        Assert.Equal(new[] { "alpha", "zeta" }, services); // 分片内按 service 排序

        Assert.True(Directory.GetFiles(Path.Combine(harness.TempDirectory, "data", "cold-idx", "logs"), "*2020110108.idx.parquet").Length > 0);
        Assert.True(Directory.GetFiles(Path.Combine(harness.TempDirectory, "data", "cold-idx", "logs"), "*2020110109.idx.parquet").Length > 0);
        Assert.False(Directory.GetFiles(Path.Combine(harness.TempDirectory, "data", "cold-idx", "logs"), "*2020110110.idx.parquet").Length > 0); // 空分片无索引
    }

    /// <summary>词项提取与索引对象键派生规则（纯函数）.</summary>
    [Fact]
    public void ColdTermIndex_ExtractAndKeyRules()
    {
        var (tokens, bigrams) = ColdTermIndex.ExtractTerms("支付网关连接超时 ORD42 好的");
        Assert.Equal(new[] { "ord42" }, tokens);
        Assert.Contains("支付", bigrams);
        Assert.Contains("接超", bigrams);
        Assert.Contains("超时", bigrams);

        Assert.Equal("s3://bkt/pfx/logs/20260101.idx.parquet", ColdTermIndex.ToIdxKey("s3://bkt/pfx/logs/20260101.parquet"));
        Assert.Equal("data/cold/logs/20260101.idx.parquet", ColdTermIndex.ToIdxKey("data/cold/logs/20260101.parquet"));
        Assert.Equal("20200101", ColdTermIndex.ShardNameFromIdxFile("/x/logs/20200101.idx.parquet"));
        Assert.Equal("2020010113", ColdTermIndex.ShardNameFromIdxFile("2020010113.idx.parquet"));
        Assert.Null(ColdTermIndex.ShardNameFromIdxFile("/x/logs/junk.parquet"));
        Assert.True(ArchiveService.IsHourly("hour"));
        Assert.True(ArchiveService.IsHourly("Hour"));
        Assert.False(ArchiveService.IsHourly("day"));
        Assert.False(ArchiveService.IsHourly(null));
        Assert.False(ArchiveService.IsHourly("weekly"));
    }

    /// <summary>查询列表结果.</summary>
    private static async Task<List<string>> QueryListAsync(ArchiveHarness harness, string sql)
    {
        return await harness.Manager.QueryAsync(sql, null, async reader =>
        {
            var list = new List<string>();
            while (await reader.ReadAsync(harness.Token))
            {
                list.Add(reader.GetString(0));
            }

            return list;
        }, harness.Token);
    }

    /// <summary>查询标量（long）.</summary>
    private static async Task<long> QueryScalarAsync(ArchiveHarness harness, string sql)
    {
        return await harness.Manager.QueryAsync(sql, null, async reader =>
        {
            await reader.ReadAsync(harness.Token);
            return reader.GetInt64(0);
        }, harness.Token);
    }

    /// <summary>运行时压缩覆盖：设置页保存 zstd 后归档对象按 ZSTD 压缩落盘（立即生效）.</summary>
    [Fact]
    public async Task Archive_UsesRuntimeCompressionOverride()
    {
        using var harness = new ArchiveHarness(); // 文件默认 snappy
        var day = DateTimeOffset.UtcNow.Date.AddDays(-40);
        await harness.CreateDayTableWithRowsAsync(TimeTables.LogsPrefix, day, 2);

        await harness.Settings.SaveAsync(SettingKeys.Compression, System.Text.Json.JsonSerializer.Serialize(new CompressionOptions { ArchiveParquetCodec = "zstd" }), harness.Token);
        Assert.Equal(1, await Pin(harness).Archive.RunOnceAsync(harness.Token));

        var parquetPath = Path.Combine(harness.TempDirectory, (await harness.GetLedgerAsync("logs")).Single().ObjectKey).Replace('\\', '/');
        var compressions = await harness.Manager.QueryAsync(
            $"SELECT DISTINCT compression FROM parquet_metadata('{parquetPath.Replace("'", "''")}')",
            null,
            async reader =>
            {
                var list = new List<string>();
                while (await reader.ReadAsync(harness.Token))
                {
                    list.Add(reader.GetString(0));
                }

                return list;
            }, harness.Token);
        Assert.Contains("ZSTD", compressions);
    }

    /// <summary>运行时保留覆盖：文件 cold=0（不归档，热到期直接删），设置页保存 hot=7/cold=30 后立即归档.</summary>
    [Fact]
    public async Task Archive_UsesRuntimeRetentionOverride()
    {
        using var harness = new ArchiveHarness(options => options.Retention.Logs.ColdDays = 0);
        var day = DateTimeOffset.UtcNow.Date.AddDays(-40);
        await harness.CreateDayTableWithRowsAsync(TimeTables.LogsPrefix, day, 1);

        Assert.Equal(0, await Pin(harness).Archive.RunOnceAsync(harness.Token)); // 文件配置不归档

        var plan = new RetentionPlanOptions();
        plan.Logs.HotDays = 7;
        plan.Logs.ColdDays = 30;
        await harness.Settings.SaveAsync(SettingKeys.Retention, System.Text.Json.JsonSerializer.Serialize(plan), harness.Token);
        Assert.Equal(1, await Pin(harness).Archive.RunOnceAsync(harness.Token)); // 覆盖后立即归档
        Assert.Equal(1, (await harness.GetLedgerAsync("logs")).Count);
    }

    /// <summary>运行时 S3 覆盖：设置页保存 S3 后对象键立即指向桶（无需重启）.</summary>
    [Fact]
    public void Archive_UsesRuntimeS3OverrideForObjectKey()
    {
        using var harness = new ArchiveHarness(); // 文件未配 S3
        var s3 = new S3Options
        {
            Endpoint = "http://minio.local:9000",
            Bucket = "sdata-cold",
            Prefix = "sdata/archive",
            AccessKey = "minioroot",
            SecretKey = "ts-" + Guid.NewGuid().ToString("N"), // 运行时生成的假密钥（非真实凭据）
        };
        harness.Settings.SaveAsync(SettingKeys.S3, System.Text.Json.JsonSerializer.Serialize(s3), harness.Token).GetAwaiter().GetResult();

        var key = Pin(harness).Archive.ResolveObjectKey(DataDomain.Logs, DateTimeOffset.UtcNow.Date);
        Assert.StartsWith("s3://sdata-cold/sdata/archive/logs/", key);
    }

    /// <summary>冷存档测试夹具（DuckDB + SQLite 台账 + 双服务）.</summary>
    private sealed class ArchiveHarness : IDisposable
    {
        /// <summary>临时目录.</summary>
        public readonly string TempDirectory;

        /// <summary>连接管理器.</summary>
        public readonly DuckDbConnectionManager Manager;

        /// <summary>Schema 引导.</summary>
        public readonly DuckDbSchemaBootstrapper Bootstrapper;

        /// <summary>批量写入器.</summary>
        public readonly IngestWriterService Writer;

        /// <summary>归档服务.</summary>
        public readonly ArchiveService Archive;
        public readonly ITableResolver Resolver;
        public readonly ColdLedgerCache LedgerCache;
        public readonly SystemOptions Options;

        /// <summary>保留服务.</summary>
        public readonly RetentionService Retention;

        /// <summary>运行时系统设置（覆盖保存用）.</summary>
        public readonly SystemSettingsProvider Settings;

        /// <summary>SQLite 台账上下文.</summary>
        public readonly DatabaseContext Ledger;

        /// <summary>取消令牌.</summary>
        public readonly CancellationToken Token = CancellationToken.None;

        /// <summary>
        /// Initializes a new instance of the <see cref="ArchiveHarness"/> class.
        /// </summary>
        /// <param name="configure">配置回调.</param>
        /// <param name="objectStore">可选对象存储替身（锁并发与故障注入）.</param>
        public ArchiveHarness(Action<SystemOptions>? configure = null, IObjectStore? objectStore = null)
        {
            TempDirectory = Path.Combine(Path.GetTempPath(), "sdata-archive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDirectory);
            AppConst.ContentRoot = TempDirectory;

            var options = new SystemOptions();
            Options = options;
            options.DuckDb.Path = Path.Combine(TempDirectory, "archive-test.duckdb");
            options.DuckDb.Threads = 2;
            configure?.Invoke(options);

            Manager = new DuckDbConnectionManager(options, NullLogger<DuckDbConnectionManager>.Instance);
            Manager.InitializeAsync().GetAwaiter().GetResult();
            Bootstrapper = new DuckDbSchemaBootstrapper(Manager, NullLogger<DuckDbSchemaBootstrapper>.Instance);
            Bootstrapper.ApplyAsync().GetAwaiter().GetResult();
            Writer = new IngestWriterService(new IngestBuffer(options), Manager, SystemSettingsProvider.FileOnly(options), new DefaultIdProvider(1), NullLogger<IngestWriterService>.Instance);

            var sqlitePath = Path.Combine(TempDirectory, "ledger.db");
            var dbOptions = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={sqlitePath}").Options;
            Ledger = new DatabaseContext(dbOptions);
            Ledger.Database.EnsureCreated();

            var scopeFactory = new SingleScopeFactory(Ledger);
            var settings = new SystemSettingsProvider(options, scopeFactory, new DefaultIdProvider(1), NullLogger<SystemSettingsProvider>.Instance);
            objectStore ??= new CompositeObjectStore(new LocalObjectStore(), new S3ObjectStore(options, NullLogger<S3ObjectStore>.Instance));
            var coldCache = new ColdCacheService(settings, objectStore, NullLogger<ColdCacheService>.Instance);
            var ledgerCache = new ColdLedgerCache(scopeFactory, NullLogger<ColdLedgerCache>.Instance);
            LedgerCache = ledgerCache;
            Resolver = new TableResolver(Manager, options, settings, ledgerCache, coldCache, NullLogger<TableResolver>.Instance);
            Archive = new ArchiveService(Manager, options, settings, ledgerCache, scopeFactory, NullLogger<ArchiveService>.Instance);
            Retention = new RetentionService(Manager, options, settings, objectStore, coldCache, ledgerCache, scopeFactory, NullLogger<RetentionService>.Instance);
            Settings = settings;
        }

        /// <summary>
        /// 归档/保留服务调用前的 ContentRoot 重设（收窄并行类静态竞态窗口）.
        /// </summary>
        public void PinContentRoot()
        {
            AppConst.ContentRoot = TempDirectory;
        }

        /// <summary>
        /// 建一个历史天表并插入 N 行日志.
        /// </summary>
        /// <param name="prefix">表前缀.</param>
        /// <param name="day">天.</param>
        /// <param name="rows">行数.</param>
        /// <returns>Task.</returns>
        public async Task CreateDayTableWithRowsAsync(string prefix, DateTimeOffset day, int rows)
        {
            var table = TimeTables.Name(prefix, day);
            await Manager.EnsureDayTablesAsync(prefix, new[] { day });
            var values = string.Join(", ", Enumerable.Range(0, rows).Select(i =>
                $"('{day:yyyy-MM-dd HH:mm:ss}', 'svc{i}', 'INFO', 'row{i}')"));
            await Manager.ExecuteWriteAsync(
                $"INSERT INTO {table} (ts, service, severity, body) VALUES {values}");
        }

        /// <summary>
        /// 读取台账（某域）.
        /// </summary>
        /// <param name="domain">域.</param>
        /// <returns>台账实体列表.</returns>
        public async Task<List<ColdArchiveEntity>> GetLedgerAsync(string domain)
        {
            return await Ledger.ColdArchives.Where(x => x.Domain == domain).ToListAsync();
        }

        /// <summary>
        /// 冷存对象是否已生成（本地冷存目录）.
        /// </summary>
        /// <param name="domain">域.</param>
        /// <param name="day">yyyyMMdd.</param>
        /// <returns>是否存在.</returns>
        public bool ColdObjectExists(string domain, string day)
        {
            var directory = Path.Combine(TempDirectory, "data", "cold", domain);
            return Directory.Exists(directory) && Directory.GetFiles(directory, $"{day}.parquet", SearchOption.AllDirectories).Length > 0;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Ledger.Dispose();
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

    private sealed class BlockingFailingObjectStore : IObjectStore
    {
        private readonly LocalObjectStore _inner = new();
        private int _failed;

        public TaskCompletionSource DeleteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDelete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
            => _inner.ExistsAsync(objectKey, cancellationToken);

        public Task DownloadAsync(string objectKey, string localPath, CancellationToken cancellationToken = default)
            => _inner.DownloadAsync(objectKey, localPath, cancellationToken);

        public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            if (!objectKey.EndsWith(".idx.parquet", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _failed, 1, 0) == 0)
            {
                DeleteStarted.TrySetResult();
                await ReleaseDelete.Task.WaitAsync(cancellationToken);
                throw new IOException("simulated cold-object delete failure");
            }

            await _inner.DeleteAsync(objectKey, cancellationToken);
        }
    }

    /// <summary>单例 scope 工厂（台账上下文直供）.</summary>
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
