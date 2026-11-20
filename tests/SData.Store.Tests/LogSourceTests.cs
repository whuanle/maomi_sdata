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
/// logs.source 列测试（T121）：新表含列、旧表启动迁移补列、写入与按来源过滤查询.
/// </summary>
[Collection("content-root-serial")]
    public class LogSourceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly SystemOptions _systemOptions;
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly IngestBuffer _buffer;
    private readonly IngestWriterService _writer;
    private readonly LogStore _logStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogSourceTests"/> class.
    /// </summary>
    public LogSourceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "sdata-logsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        AppConst.ContentRoot = _tempDirectory;

        _systemOptions = new SystemOptions();
        _systemOptions.DuckDb.Path = "logsrc-test.duckdb";
        _systemOptions.DuckDb.Threads = 2;

        _connectionManager = new DuckDbConnectionManager(_systemOptions, NullLogger<DuckDbConnectionManager>.Instance);
        _connectionManager.InitializeAsync().GetAwaiter().GetResult();

        _buffer = new IngestBuffer(_systemOptions);
        _writer = new IngestWriterService(_buffer, _connectionManager, _systemOptions, new SData.Infra.Services.DefaultIdProvider(1), NullLogger<IngestWriterService>.Instance);

        // 统一查询管线依赖（内存台账镜像：本测试无台账行，冷层恒空，行为与旧版一致）
        var sqlitePath = Path.Combine(_tempDirectory, "ledger.db");
        var dbOptions = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={sqlitePath}").Options;
        var ledger = new DatabaseContext(dbOptions);
        ledger.Database.EnsureCreated();
        var scopeFactory = new SingleScopeFactory(ledger);
        var settings = new SystemSettingsProvider(_systemOptions, scopeFactory, new DefaultIdProvider(1), NullLogger<SystemSettingsProvider>.Instance);
        var objectStore = new CompositeObjectStore(new LocalObjectStore(), new S3ObjectStore(_systemOptions, NullLogger<S3ObjectStore>.Instance));
        var coldCache = new ColdCacheService(_systemOptions, objectStore, NullLogger<ColdCacheService>.Instance);
        var ledgerCache = new ColdLedgerCache(scopeFactory, NullLogger<ColdLedgerCache>.Instance);
        _logStore = new LogStore(_connectionManager, _systemOptions, new TableResolver(_connectionManager, _systemOptions, settings, ledgerCache, coldCache, NullLogger<TableResolver>.Instance));
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

    /// <summary>
    /// 新建 logs 天表含行级 id 列与 source 列（10 列）.
    /// </summary>
    [Fact]
    public void NewLogTable_ContainsSourceColumn()
    {
        var createSql = TimeTables.CreateSql(TimeTables.Name(TimeTables.LogsPrefix, DateTimeOffset.UtcNow));
        Assert.Contains("id BIGINT", createSql[0], StringComparison.Ordinal);
        Assert.Contains("source VARCHAR", createSql[0], StringComparison.Ordinal);

        var columns = GetColumns("logs_19000101");
        // 建表后验证物理列
        _connectionManager.EnsureDayTablesAsync(TimeTables.LogsPrefix, new[] { new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero) }).GetAwaiter().GetResult();
        columns = GetColumns("logs_19000101");
        Assert.Contains("id", columns);
        Assert.Contains("source", columns);
        Assert.Equal(10, columns.Count);
    }

    /// <summary>
    /// 旧版 8 列 logs 表经 Schema 引导迁移后补 source/id 列（10 列）.
    /// </summary>
    [Fact]
    public void LegacyLogTable_MigratedWithSource()
    {
        var table = TimeTables.Name(TimeTables.LogsPrefix, new DateTimeOffset(1980, 5, 6, 0, 0, 0, TimeSpan.Zero));
        _connectionManager.ExecuteWriteAsync(
            $"CREATE TABLE {table} (ts TIMESTAMP NOT NULL, service VARCHAR NOT NULL, severity VARCHAR NOT NULL, body VARCHAR NOT NULL, trace_id VARCHAR, span_id VARCHAR, attributes VARCHAR, resource VARCHAR)",
            cancellationToken: TestCancellationToken).GetAwaiter().GetResult();

        // 模拟旧库：此时该表为 8 列
        Assert.DoesNotContain("source", GetColumns(table));

        // Schema 引导（含旧表迁移）后补列
        new DuckDbSchemaBootstrapper(_connectionManager, NullLogger<DuckDbSchemaBootstrapper>.Instance)
            .ApplyAsync().GetAwaiter().GetResult();

        var columns = GetColumns(table);
        Assert.Contains("source", columns);
        Assert.Contains("id", columns);
        Assert.Equal(10, columns.Count);
        _writer.FlushAsync(new List<IngestItem>
        {
            new(IngestKind.Log, new LogRecord
            {
                Timestamp = new DateTimeOffset(1980, 5, 6, 12, 0, 0, TimeSpan.Zero),
                Service = "migrated", Body = "legacy append", Source = "kafka:legacy",
            }),
        }, TestCancellationToken).GetAwaiter().GetResult();
        using var connection = _connectionManager.CloneConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT service, body, source, id, ts, severity, trace_id, span_id, attributes, resource FROM {table}";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("migrated", reader.GetString(0));
        Assert.Equal("legacy append", reader.GetString(1));
        Assert.Equal("kafka:legacy", reader.GetString(2));
        Assert.True(reader.GetInt64(3) > 0);
        Assert.Equal(new DateTime(1980, 5, 6, 12, 0, 0), reader.GetDateTime(4));
        Assert.Equal("INFO", reader.GetString(5));
        for (var i = 6; i < 10; i++) Assert.True(reader.IsDBNull(i));
    }

    /// <summary>
    /// 写入含 Source 的日志并按来源过滤查询；无来源记录不受影响.
    /// </summary>
    [Fact]
    public async Task WriteAndQueryBySource()
    {
        var now = DateTimeOffset.UtcNow;
        var withSource = new LogRecord
        {
            Timestamp = now,
            Service = "filebeat",
            Severity = "INFO",
            Body = "{\"message\":\"from es bulk\"}",
            Source = "logs-2026.11.15",
            AttributesJson = "{\"_index\":\"logs-2026.11.15\"}",
        };
        var withoutSource = new LogRecord
        {
            Timestamp = now.AddMilliseconds(-1),
            Service = "api",
            Severity = "WARN",
            Body = "plain log",
        };

        var batch = new List<IngestItem>
        {
            new(IngestKind.Log, withSource),
            new(IngestKind.Log, withoutSource),
        };
        await _writer.FlushAsync(batch, TestCancellationToken);

        var bySource = await _logStore.QueryAsync(new LogQuery
        {
            Start = now.AddMinutes(-1),
            End = now.AddMinutes(1),
            Source = "logs-2026.11.15",
            Limit = 10,
        }, TestCancellationToken);
        var record = Assert.Single(bySource);
        Assert.Equal("logs-2026.11.15", record.Source);
        Assert.Equal("filebeat", record.Service);

        var all = await _logStore.QueryAsync(new LogQuery
        {
            Start = now.AddMinutes(-1),
            End = now.AddMinutes(1),
            Limit = 10,
        }, TestCancellationToken);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, x => x.Source == null);
    }

    /// <summary>
    /// 服务端分页：OFFSET 下推到 SQL，page&gt;1 能取到第二段（修复内存 Skip 导致的空页）.
    /// </summary>
    [Fact]
    public async Task QueryAsync_OffsetPaging_ReturnsSecondPage()
    {
        var now = DateTimeOffset.UtcNow;
        var batch = Enumerable.Range(0, 5).Select(i => new IngestItem(IngestKind.Log,
            new LogRecord
            {
                Timestamp = now.AddSeconds(-i),
                Service = "page-svc",
                Severity = "INFO",
                Body = "page-" + i,
            })).ToList();
        await _writer.FlushAsync(batch, TestCancellationToken);

        var range = new LogQuery
        {
            Start = now.AddMinutes(-1),
            End = now.AddMinutes(1),
            Service = "page-svc",
            Limit = 2,
        };

        var first = await _logStore.QueryAsync(new LogQuery
        {
            Start = range.Start, End = range.End, Service = range.Service, Limit = 2, Offset = 0,
        }, TestCancellationToken);
        var second = await _logStore.QueryAsync(new LogQuery
        {
            Start = range.Start, End = range.End, Service = range.Service, Limit = 2, Offset = 2,
        }, TestCancellationToken);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal("page-0", first[0].Body);
        Assert.Equal("page-1", first[1].Body);
        Assert.Equal("page-2", second[0].Body);
        Assert.Equal("page-3", second[1].Body);
    }

    /// <summary>
    /// 遗留表与新表混合区间查询（UNION 补列后语义一致）.
    /// </summary>
    [Fact]
    public async Task MixedLegacyAndNewTables_QueryUnified()
    {
        var legacyDay = new DateTimeOffset(1980, 5, 6, 12, 0, 0, TimeSpan.Zero);
        var legacyTable = TimeTables.Name(TimeTables.LogsPrefix, legacyDay);

        // 自建 8 列旧表 → 注册进表缓存（模拟重启后 InitializeAsync 的既有表登记）→ 迁移补列 → 写入旧行（source 为 NULL）
        _connectionManager.ExecuteWriteAsync(
            $"CREATE TABLE {legacyTable} (ts TIMESTAMP NOT NULL, service VARCHAR NOT NULL, severity VARCHAR NOT NULL, body VARCHAR NOT NULL, trace_id VARCHAR, span_id VARCHAR, attributes VARCHAR, resource VARCHAR)",
            cancellationToken: TestCancellationToken).GetAwaiter().GetResult();
        _connectionManager.EnsureDayTablesAsync(TimeTables.LogsPrefix, new[] { legacyDay }, TestCancellationToken).GetAwaiter().GetResult();
        new DuckDbSchemaBootstrapper(_connectionManager, NullLogger<DuckDbSchemaBootstrapper>.Instance)
            .ApplyAsync().GetAwaiter().GetResult();
        _connectionManager.ExecuteWriteAsync(
            $"INSERT INTO {legacyTable} (ts, service, severity, body) VALUES ('1980-05-06 12:00:00', 'legacy-svc', 'INFO', 'old row')",
            cancellationToken: TestCancellationToken).GetAwaiter().GetResult();

        var results = await _logStore.QueryAsync(new LogQuery
        {
            Start = new DateTimeOffset(1980, 5, 6, 0, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(1980, 5, 7, 0, 0, 0, TimeSpan.Zero),
            Service = "legacy-svc",
        }, TestCancellationToken);

        var row = Assert.Single(results);
        Assert.Equal("legacy-svc", row.Service);
        Assert.Null(row.Source);
    }

    private static CancellationToken TestCancellationToken => CancellationToken.None;

    private IReadOnlyList<string> GetColumns(string table)
    {
        var columns = new List<string>();
        using var connection = _connectionManager.CloneConnection();
        using var command = ((DuckDB.NET.Data.DuckDBConnection)connection).CreateCommand();
        command.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_name = $table";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "table";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, true);
        }
        catch (IOException)
        {
        }
    }
}
