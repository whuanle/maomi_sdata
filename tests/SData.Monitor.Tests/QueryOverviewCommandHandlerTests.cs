using global::Xunit;
using SData.Infra.Configuration;
using SData.Monitor.Handlers;
using SData.Store;
using SData.Settings;
using SData.Ingest;
using System.Text.Json;
using SData.Store.Core;
using SData.Store.Models;

namespace SData.Monitor.Tests;

/// <summary>
/// 总览聚合测试（T314）：通道健康/今日量/存储用量聚合.
/// </summary>
public class QueryOverviewCommandHandlerTests
{
    [Fact]
    public async Task Handle_AggregatesChannelsTodayRowsAndStorage()
    {
        var today = DateTimeOffset.UtcNow;
        var todayTable = TimeTables.Name(TimeTables.LogsPrefix, today);
        var buffer = new StubBuffer();
        var storeInfo = new StubStoreInfo(new Dictionary<string, IReadOnlyList<string>>
        {
            [TimeTables.LogsPrefix] = [todayTable],
            [TimeTables.TracesPrefix] = ["traces_20261114"],
            [TimeTables.MetricsPrefix] = [],
            [TimeTables.IotPrefix] = [],
            [TimeTables.RumEventPrefix] = [],
            [TimeTables.RumSessionPrefix] = [],
            [TimeTables.RumReplayPrefix] = [],
        });
        storeInfo.Counts[todayTable] = 42;
        var options = new SystemOptions
        {
            DuckDb = new DuckDbOptions { Path = "data/overview-test.duckdb", MemoryLimit = "1GB" },
        };

        var handler = new QueryOverviewCommandHandler(buffer, storeInfo, options, new StubSettings(), new StubMetrics(), new StubDuckDb());
        var response = await handler.Handle(new QueryOverviewCommand(), CancellationToken.None);

        // 今日量：logs 今日表 42，traces 今日表不存在为 0
        Assert.Equal(42, response.TodayRows["logs"]);
        Assert.Equal(0, response.TodayRows["traces"]);
        Assert.Equal(1, response.TableCounts["logs"]);

        // 通道健康：7 通道，缓冲统计按 kind 小写键聚合，rum 前缀映射无下划线键
        Assert.Equal(7, response.Channels.Count);
        var logChannel = Assert.Single(response.Channels, x => x.Name == "logs");
        Assert.Equal(100, logChannel.Processed);
        Assert.Equal(0, logChannel.Dropped);
        Assert.True(logChannel.Healthy);
        var rumEventChannel = Assert.Single(response.Channels, x => x.Name == "rum_event");
        Assert.Equal(5, rumEventChannel.Dropped);
        Assert.False(rumEventChannel.Healthy);

        // 存储用量：配置路径与内存上限透传；文件不存在时字节为 0
        Assert.Equal("1.8 GiB", response.Storage.MemoryLimit);
        Assert.Equal("data/overview-test.duckdb", response.Storage.HotDbPath);
        Assert.Equal(0, response.Storage.HotDbBytes);
        Assert.False(response.Storage.ColdEnabled);
    }

    [Fact]
    public async Task Handle_UsesEffectiveSettingsAndReturnsSafeConsumerSnapshots()
    {
        var enabledId = Random.Shared.NextInt64(1_000_000, long.MaxValue);
        var deletedId = enabledId - 1;
        MqRuntime.Persisted(enabledId, 12);
        MqRuntime.SetState(enabledId, "error", "amqp://user:secret@host/token");
        MqRuntime.SetState(deletedId, "stopped");
        var settings = new StubSettings
        {
            S3 = new S3Options { Endpoint = "s3.example", Bucket = "archive", AccessKey = "private-key", SecretKey = "private-secret" },
            Instances =
            [
                new MqInstanceOptions { Id = enabledId, Name = "logs-source", Type = "rabbitmq", Enabled = true, Uri = "amqp://user:secret@host" },
                new MqInstanceOptions { Id = enabledId + 1, Name = "disabled-source", Enabled = false },
                new MqInstanceOptions { Id = enabledId + 2, Name = "pending-source", Enabled = true },
            ],
        };
        var buffer = new StubBuffer();
        buffer.Stats.Buffered["log"] = 9;
        buffer.Stats.Buffered["trace"] = 9;
        var handler = new QueryOverviewCommandHandler(buffer, new StubStoreInfo(new()), new SystemOptions(), settings, new StubMetrics(), new StubDuckDb());

        var response = await handler.Handle(new QueryOverviewCommand(), CancellationToken.None);

        Assert.True(response.Storage.ColdEnabled);
        Assert.Equal(9, response.BufferedRecords);
        Assert.Equal(3, response.MqConsumers.Count);
        Assert.DoesNotContain(response.MqConsumers, x => x.InstanceId == deletedId);
        var consumer = Assert.Single(response.MqConsumers, x => x.InstanceId == enabledId);
        Assert.Equal("logs-source", consumer.Name);
        Assert.Equal("rabbitmq", consumer.Type);
        Assert.Equal("error", consumer.State);
        Assert.Equal(12, consumer.PersistedRecords);
        Assert.NotNull(consumer.LastPersistedAt);
        Assert.NotNull(consumer.LastError);
        Assert.Equal("disabled", response.MqConsumers[1].State);
        Assert.Equal("unknown", response.MqConsumers[2].State);
        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("private-key", json);
        Assert.DoesNotContain("private-secret", json);
        Assert.DoesNotContain("amqp://", json);
        Assert.DoesNotContain("user:secret", json);
    }

    [Theory]
    [InlineData("", "archive")]
    [InlineData("s3.example", "")]
    [InlineData(" ", "archive")]
    public async Task Handle_IncompleteS3ConfigurationIsNotEnabled(string endpoint, string bucket)
    {
        var settings = new StubSettings { S3 = new S3Options { Endpoint = endpoint, Bucket = bucket } };
        var handler = new QueryOverviewCommandHandler(new StubBuffer(), new StubStoreInfo(new()), new SystemOptions(), settings, new StubMetrics(), new StubDuckDb());
        var response = await handler.Handle(new QueryOverviewCommand(), CancellationToken.None);
        Assert.False(response.Storage.ColdEnabled);
        Assert.Empty(response.MqConsumers);
    }

    [Fact]
    public async Task Handle_CachesTodayRowCountsForTtl()
    {
        var today = DateTimeOffset.UtcNow;
        var storeInfo = new StubStoreInfo(new Dictionary<string, IReadOnlyList<string>>
        {
            [TimeTables.LogsPrefix] = [TimeTables.Name(TimeTables.LogsPrefix, today)],
        });
        storeInfo.Counts[TimeTables.Name(TimeTables.LogsPrefix, today)] = 42;
        var handler = new QueryOverviewCommandHandler(
            new StubBuffer(),
            storeInfo,
            new SystemOptions(),
            new StubSettings(),
            new StubMetrics(),
            new StubDuckDb());

        await handler.Handle(new QueryOverviewCommand(), CancellationToken.None);
        await handler.Handle(new QueryOverviewCommand(), CancellationToken.None);

        Assert.Equal(1, storeInfo.CountTablesCalls);
        Assert.Equal(42, storeInfo.Counts[TimeTables.Name(TimeTables.LogsPrefix, today)]);
    }

    private sealed class StubSettings : ISystemSettingsProvider
    {
        public S3Options S3 { get; set; } = new();
        public List<MqInstanceOptions> Instances { get; set; } = new();
        public DuckDbSettings GetDuckDb() => new();
        public RuntimeSettings GetRuntime() => new();
        public S3Options GetS3() => S3;
        public List<MqInstanceOptions> GetMqInstances() => Instances;
        public RetentionPlanOptions GetRetention() => new();
        public CompressionOptions GetCompression() => new();
        public OtlpOptions GetOtlp() => new();
        public Task SaveAsync(string key, string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubDuckDb : IDuckDbRuntimeSettings
    {
        public string MemoryLimit => "2GB";
        public string EffectiveMemoryLimit => "1.8 GiB";
        public Task ApplyMemoryLimitAsync(string memoryLimit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ApplyThreadsAsync(int threads, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void ApplyMaxConcurrentQueries(int maxConcurrentQueries) => throw new NotSupportedException();
    }

    private sealed class StubMetrics : IStorageMetrics
    {
        public LocalStorageSnapshot Local { get; } = new();
        public S3StorageSnapshot S3 { get; } = new();
    }

    private sealed class StubBuffer : IIngestBuffer
    {
        public IngestBufferStats Stats { get; set; } = new();

        public bool TryEnqueueLog(LogRecord record) => true;

        public bool TryEnqueueSpan(SpanRecord record) => true;

        public bool TryEnqueueMetric(MetricPointRecord record) => true;

        public bool TryEnqueueTelemetry(TelemetryRecord record) => true;

        public bool TryEnqueueRumEvent(RumEventRecord record) => true;

        public bool TryEnqueueRumSession(RumSessionRecord record) => true;

        public bool TryEnqueueRumReplay(RumReplayRecord record) => true;

        public IngestBufferStats GetStats()
        {
            Stats.Processed["log"] = 100;
            Stats.Dropped["rumevent"] = 5;
            return Stats;
        }
    }

    private sealed class StubStoreInfo(Dictionary<string, IReadOnlyList<string>> tables) : IStoreInfo
    {
        public Dictionary<string, long> Counts { get; } = new();
        public int CountTablesCalls { get; private set; }

        public Task<long> CountTableAsync(string tableName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Counts.GetValueOrDefault(tableName));
        }

        public Task<IReadOnlyDictionary<string, long>> CountTablesAsync(IReadOnlyList<(string Name, string TableName)> tables, CancellationToken cancellationToken = default)
        {
            CountTablesCalls++;
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (name, table) in tables)
            {
                result[name] = Counts.GetValueOrDefault(table);
            }
            return Task.FromResult<IReadOnlyDictionary<string, long>>(result);
        }

        public IReadOnlyList<string> GetTableNames(string prefix)
        {
            return tables.GetValueOrDefault(prefix, []);
        }
    }
}
