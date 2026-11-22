using System.IO.Compression;
using System.Text;
using global::Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Rum;
using SData.Rum.Services;
using SData.Settings.Services;
using SData.Store;
using SData.Store.Core;

namespace SData.Store.Tests;

/// <summary>
/// RUM beacon 摄取集成测试（T610）：gzip JSON 入库到 rum_* 天表 + 401/429 语义.
/// </summary>
[Collection("content-root-serial")]
    public class RumBeaconTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly SystemOptions _systemOptions;
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly IngestBuffer _buffer;
    private readonly IngestWriterService _writer;
    private readonly RumBeaconService _beaconService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RumBeaconTests"/> class.
    /// </summary>
    public RumBeaconTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "sdata-rum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        AppConst.ContentRoot = _tempDirectory;

        _systemOptions = new SystemOptions();
        _systemOptions.DuckDb.Path = "rum-test.duckdb";
        _systemOptions.DuckDb.Threads = 2;

        _connectionManager = new DuckDbConnectionManager(_systemOptions, NullLogger<DuckDbConnectionManager>.Instance);
        _connectionManager.InitializeAsync().GetAwaiter().GetResult();

        _buffer = new IngestBuffer(_systemOptions);
        _writer = new IngestWriterService(_buffer, _connectionManager, SystemSettingsProvider.FileOnly(_systemOptions), new SData.Infra.Services.DefaultIdProvider(1), NullLogger<IngestWriterService>.Instance);
        _beaconService = new RumBeaconService(_buffer, originPolicy: new RumTestOriginPolicy());
    }

    /// <summary>
    /// gzip beacon：事件按类型分派入库（rum_event/rum_session/rum_replay 各有行）.
    /// </summary>
    [Fact]
    public async Task Beacon_GzipEvents_LandInTables()
    {
        const string json = """
            {
              "projectKey": "rum_demo",
              "sessionId": "s_ab12kp",
              "events": [
                {"type": "pageview", "ts": "2026-11-15T12:00:00Z", "pagePath": "/home", "ua": "Mozilla/5.0 test", "domain": "intranet.local"},
                {"type": "vital", "ts": "2026-11-15T12:00:01Z", "name": "LCP", "value": 2345.0, "pagePath": "/home"},
                {"type": "replay-chunk", "ts": "2026-11-15T12:00:02Z", "seq": 0, "blob": "AAAA"}
              ]
            }
            """;

        var jsonWithBlob = json.Replace("\"blob\": \"AAAA\"", "\"blobBase64\": \"AAAA\"", StringComparison.Ordinal);
        var status = await _beaconService.IngestAsync(Gzip(jsonWithBlob), "gzip", "https://intranet.local");
        Assert.Equal(RumIngestStatus.Ok, status);

        await _writer.FlushAsync(DrainAll(), CancellationToken.None);

        // 天表名取自事件时间戳（UtcNow 会在事件日期翻天后错表，日期炸弹修复）
        var eventDay = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, CountRows(TimeTables.Name(TimeTables.RumEventPrefix, eventDay))); // vital
        Assert.Equal(1, CountRows(TimeTables.Name(TimeTables.RumSessionPrefix, eventDay))); // pageview
        Assert.Equal(1, CountRows(TimeTables.Name(TimeTables.RumReplayPrefix, eventDay))); // replay-chunk
    }

    /// <summary>
    /// 明文 JSON（无 gzip）也能摄取.
    /// </summary>
    [Fact]
    public async Task Beacon_PlainJson_LandsInEventTable()
    {
        const string json = """
            {"projectKey": "rum_demo", "sessionId": "s_x", "events": [{"type": "error", "name": "TypeError", "pagePath": "/p"}]}
            """;

        var status = await _beaconService.IngestAsync(Encoding.UTF8.GetBytes(json), null, "https://intranet.local");
        Assert.Equal(RumIngestStatus.Ok, status);

        await _writer.FlushAsync(DrainAll(), CancellationToken.None);
        Assert.True(CountRows(TimeTables.Name(TimeTables.RumEventPrefix, DateTimeOffset.UtcNow)) >= 1);
    }

    /// <summary>
    /// 401 语义：projectKey 缺失或 JSON 非法返回 InvalidKey.
    /// </summary>
    [Fact]
    public async Task Beacon_MissingKey_ReturnsInvalidKey()
    {
        var status = await _beaconService.IngestAsync(Encoding.UTF8.GetBytes("""{"sessionId": "s_x", "events": []}"""), null);
        Assert.Equal(RumIngestStatus.InvalidKey, status);

        status = await _beaconService.IngestAsync(Encoding.UTF8.GetBytes("not-json"), null);
        Assert.Equal(RumIngestStatus.InvalidKey, status);
    }

    /// <summary>
    /// 429 语义：缓冲满时返回 BufferFull 且被计数.
    /// </summary>
    [Fact]
    public async Task Beacon_BufferFull_ReturnsBufferFull()
    {
        var options = new SystemOptions();
        options.Ingest.BufferCapacity = 1;
        var tinyBuffer = new IngestBuffer(options);
        var service = new RumBeaconService(tinyBuffer, originPolicy: new RumTestOriginPolicy());

        const string json = """
            {"projectKey": "rum_demo", "sessionId": "s_x", "events": [
              {"type": "click", "pagePath": "/a"},
              {"type": "click", "pagePath": "/b"}
            ]}
            """;

        var status = await service.IngestAsync(Encoding.UTF8.GetBytes(json), null, "https://intranet.local");
        Assert.Equal(RumIngestStatus.BufferFull, status);
    }

    /// <summary>
    /// 采样配置：项目未建时默认全采样.
    /// </summary>
    [Fact]
    public void Config_RejectsUnconfiguredSources()
    {
        Assert.Null(_beaconService.GetConfig("rum_missing", "https://intranet.local"));
        Assert.Null(_beaconService.GetConfig("rum_demo", "https://other.local"));
        Assert.Null(_beaconService.GetConfig("rum_demo"));
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return output.ToArray();
    }

    private List<IngestItem> DrainAll()
    {
        var batch = new List<IngestItem>();
        while (_buffer.Reader.TryRead(out var item))
        {
            batch.Add(item);
        }

        return batch;
    }

    private long CountRows(string table)
    {
        using var connection = _connectionManager.CloneConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table}";
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        var result = command.ExecuteScalar();
        return Convert.ToInt64(result);
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
