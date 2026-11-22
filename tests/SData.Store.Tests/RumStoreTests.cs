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
/// RUM 查询聚合集成测试（T613）：beacon 写入 → PV/UV、Web Vitals p75、错误 Top、会话列表.
/// </summary>
[Collection("content-root-serial")]
    public class RumStoreTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly SystemOptions _systemOptions;
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly IngestBuffer _buffer;
    private readonly IngestWriterService _writer;
    private readonly RumBeaconService _beaconService;
    private readonly RumStore _rumStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="RumStoreTests"/> class.
    /// </summary>
    public RumStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "sdata-rumq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        AppConst.ContentRoot = _tempDirectory;

        _systemOptions = new SystemOptions();
        _systemOptions.DuckDb.Path = "rum-query-test.duckdb";
        _systemOptions.DuckDb.Threads = 2;

        _connectionManager = new DuckDbConnectionManager(_systemOptions, NullLogger<DuckDbConnectionManager>.Instance);
        _connectionManager.InitializeAsync().GetAwaiter().GetResult();

        _buffer = new IngestBuffer(_systemOptions);
        _writer = new IngestWriterService(_buffer, _connectionManager, SystemSettingsProvider.FileOnly(_systemOptions), new SData.Infra.Services.DefaultIdProvider(1), NullLogger<IngestWriterService>.Instance);
        _beaconService = new RumBeaconService(_buffer, originPolicy: new RumTestOriginPolicy());
        _rumStore = new RumStore(_connectionManager);
    }

    /// <summary>
    /// 写入 beacon 后聚合：PV/UV、vital p75、错误 Top、会话列表全链路.
    /// </summary>
    [Fact]
    public async Task Aggregate_AfterBeaconIngest_ProducesExpectedMetrics()
    {
        await IngestAsync("rum_demo", "s_aaa", """
            [
              {"type": "pageview", "ts": "2026-11-15T12:00:00Z", "pagePath": "/home", "extra": {"ua": "UA-1", "domain": "a.local"}},
              {"type": "vital", "ts": "2026-11-15T12:00:01Z", "name": "LCP", "value": 1000.0, "pagePath": "/home"},
              {"type": "vital", "ts": "2026-11-15T12:00:02Z", "name": "LCP", "value": 2000.0, "pagePath": "/home"},
              {"type": "vital", "ts": "2026-11-15T12:00:03Z", "name": "LCP", "value": 3000.0, "pagePath": "/home"},
              {"type": "vital", "ts": "2026-11-15T12:00:04Z", "name": "CLS", "value": 0.1, "pagePath": "/home"},
              {"type": "error", "ts": "2026-11-15T12:00:05Z", "name": "TypeError: x is undefined", "pagePath": "/home"},
              {"type": "error", "ts": "2026-11-15T12:00:06Z", "name": "TypeError: x is undefined", "pagePath": "/home"},
              {"type": "error", "ts": "2026-11-15T12:00:07Z", "name": "ReferenceError: y is not defined", "pagePath": "/cart"}
            ]
            """);
        await IngestAsync("rum_demo", "s_bbb", """
            [
              {"type": "pageview", "ts": "2026-11-15T12:01:00Z", "pagePath": "/cart", "extra": {"ua": "UA-2", "domain": "a.local"}}
            ]
            """);

        var from = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

        // PV = pageview 行数（2 次会话各 1 次），UV = distinct 会话（2）
        var performance = await _rumStore.AggregatePerformanceAsync("rum_demo", from, to);
        Assert.Equal(2, performance.PageViews);
        Assert.Equal(2, performance.UniqueVisitors);

        // LCP 4 个值 [1000,2000,3000] —— 实际 3 个；p75（插值）= 2500；CLS p75 = 0.1
        Assert.Equal(2500, performance.VitalP75["LCP"]);
        Assert.Equal(0.1, performance.VitalP75["CLS"]);

        // 错误 Top：TypeError 2 次排第一
        var errors = await _rumStore.AggregateErrorsAsync("rum_demo", from, to, 10);
        Assert.Equal(2, errors.Count);
        Assert.Equal("TypeError: x is undefined", errors[0].Name);
        Assert.Equal(2, errors[0].Count);
        Assert.Equal("ReferenceError: y is not defined", errors[1].Name);

        // 会话列表：按最近活跃降序，含 UA/domain
        var sessions = await _rumStore.ListSessionsAsync("rum_demo", from, to, 10);
        Assert.Equal(2, sessions.Count);
        Assert.Equal("s_bbb", sessions[0].SessionId);
        Assert.Equal("s_aaa", sessions[1].SessionId);
        Assert.Equal("UA-1", sessions[1].UserAgent);
        Assert.Equal("a.local", sessions[1].Domain);

        // 项目隔离：其他项目无数据
        var otherPerformance = await _rumStore.AggregatePerformanceAsync("rum_other", from, to);
        Assert.Equal(0, otherPerformance.PageViews);
        Assert.Empty(otherPerformance.VitalP75);
    }

    /// <summary>
    /// 无任何 rum 表（空库）时聚合短路返回零值.
    /// </summary>
    [Fact]
    public async Task Aggregate_NoTables_ReturnsEmpty()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

        var performance = await _rumStore.AggregatePerformanceAsync("rum_demo", from, to);
        Assert.Equal(0, performance.PageViews);
        Assert.Equal(0, performance.UniqueVisitors);
        Assert.Empty(performance.VitalP75);

        Assert.Empty(await _rumStore.AggregateErrorsAsync("rum_demo", from, to, 10));
        Assert.Empty(await _rumStore.ListSessionsAsync("rum_demo", from, to, 10));
        Assert.Empty(await _rumStore.GetReplayAsync("rum_demo", "s_none", from, to));
    }

    /// <summary>
    /// 会话回放：replay-chunk 摄取后按 seq 升序读取（T614）.
    /// </summary>
    [Fact]
    public async Task GetReplay_AfterChunkIngest_ReturnsOrderedChunks()
    {
        await IngestAsync("rum_demo", "s_replay", """
            [
              {"type": "replay-chunk", "ts": "2026-11-15T12:00:01Z", "seq": 2, "blobBase64": "H4sIAAAAAAAACmNoYW5rLTI"},
              {"type": "replay-chunk", "ts": "2026-11-15T12:00:00Z", "seq": 1, "blobBase64": "H4sIAAAAAAAACmNoYW5rLTE"},
              {"type": "replay-chunk", "ts": "2026-11-15T12:00:02Z", "seq": 3, "blobBase64": "H4sIAAAAAAAACmNoYW5rLTM"}
            ]
            """);

        var from = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

        var chunks = await _rumStore.GetReplayAsync("rum_demo", "s_replay", from, to);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(1, chunks[0].Seq);
        Assert.Equal(2, chunks[1].Seq);
        Assert.Equal(3, chunks[2].Seq);
        Assert.Equal("H4sIAAAAAAAACmNoYW5rLTE", chunks[0].BlobBase64);
        Assert.Equal("H4sIAAAAAAAACmNoYW5rLTM", chunks[2].BlobBase64);

        // 其他会话无回放
        Assert.Empty(await _rumStore.GetReplayAsync("rum_demo", "s_other", from, to));
    }


    /// <summary>
    /// 热力栅格与页面树（T615）：click 坐标 extra 形态入库 → 栅格分桶与页面聚合.
    /// </summary>
    [Fact]
    public async Task Heatmap_And_Sitemap_AggregateClicks()
    {
        await IngestAsync("rum_demo", "s_click", """
            [
              {"type": "pageview", "ts": "2026-11-15T12:00:00Z", "pagePath": "/home", "extra": {"ua": "UA-9", "domain": "a.local"}},
              {"type": "click", "ts": "2026-11-15T12:00:01Z", "pagePath": "/home", "name": "button", "extra": {"x": 990, "y": 55, "pageW": 1000, "pageH": 1000}},
              {"type": "click", "ts": "2026-11-15T12:00:02Z", "pagePath": "/home", "name": "button", "extra": {"x": 985, "y": 60, "pageW": 1000, "pageH": 1000}},
              {"type": "click", "ts": "2026-11-15T12:00:03Z", "pagePath": "/cart", "name": "a", "extra": {"x": 100, "y": 500, "pageW": 1000, "pageH": 1000}},
              {"type": "error", "ts": "2026-11-15T12:00:04Z", "name": "boom", "pagePath": "/cart"}
            ]
            """);

        var from = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

        // 栅格（20x20）：(990,55)→(19,1)、(985,60)→(19,1) 同桶合并；(100,500)→(2,10)
        var cells = (await _rumStore.AggregateHeatmapAsync("rum_demo", from, to, 20)).Cells;
        Assert.Equal(2, cells.Count);
        var hot = cells[0];
        Assert.Equal(19, hot.X);
        Assert.Equal(1, hot.Y);
        Assert.Equal(2, hot.Count);

        // 页面树：/home 2 click、/cart 1 click 1 error
        var pages = await _rumStore.AggregatePageTreeAsync("rum_demo", from, to, 10);
        Assert.Equal(2, pages.Count);
        Assert.Equal("/home", pages[0].PagePath);
        Assert.Equal(2, pages[0].Clicks);
        Assert.Equal(0, pages[0].Errors);
        Assert.Equal("/cart", pages[1].PagePath);
        Assert.Equal(1, pages[1].Clicks);
        Assert.Equal(1, pages[1].Errors);
    }

    /// <summary>
    /// 热力增强（T616）：文档绝对坐标优先/视口坐标回退、页面路径过滤、点击总数与目标 TopN.
    /// </summary>
    [Fact]
    public async Task Heatmap_DocumentCoords_PageFilter_And_TopTargets()
    {
        await IngestAsync("rum_demo", "s_t616", """
            [
              {"type": "pageview", "ts": "2026-11-15T12:00:00Z", "pagePath": "/home", "extra": {"ua": "UA-16", "domain": "a.local"}},
              {"type": "click", "ts": "2026-11-15T12:00:01Z", "pagePath": "/home", "name": "button", "extra": {"x": 500, "y": 100, "pageX": 500, "pageY": 1000, "docW": 1000, "docH": 2000, "selector": "button#submit-btn", "targetId": "submit-btn"}},
              {"type": "click", "ts": "2026-11-15T12:00:02Z", "pagePath": "/home", "name": "button", "extra": {"x": 520, "y": 110, "pageX": 520, "pageY": 1020, "docW": 1000, "docH": 2000, "selector": "button#submit-btn"}},
              {"type": "click", "ts": "2026-11-15T12:00:03Z", "pagePath": "/cart", "name": "a", "extra": {"x": 990, "y": 55, "pageW": 1000, "pageH": 1000, "targetId": "checkout"}},
              {"type": "click", "ts": "2026-11-15T12:00:04Z", "pagePath": "/cart", "name": "span"}
            ]
            """);

        var from = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

        // 全站：总数只计有坐标的点击（4 条中 3 条）；文档坐标 (500,1000)/(1000,2000)→(10,10)、(520,1020)→(10,10) 同桶 2 次；视口回退 (990,55)/(1000,1000)→(19,1)
        var all = await _rumStore.AggregateHeatmapAsync("rum_demo", from, to, 20);
        Assert.Equal(3, all.TotalClicks);
        Assert.Equal(2, all.Cells.Count);
        Assert.Contains(all.Cells, c => c.X == 10 && c.Y == 10 && c.Count == 2);
        Assert.Contains(all.Cells, c => c.X == 19 && c.Y == 1 && c.Count == 1);

        // 页面过滤：仅 /home（文档坐标桶）
        var home = await _rumStore.AggregateHeatmapAsync("rum_demo", from, to, 20, "/home");
        Assert.Equal(2, home.TotalClicks);
        Assert.All(home.Cells, c => Assert.Equal(10, c.X));

        // 目标 TopN：selector 优先，回退 targetId；无目标信息（span）不计
        Assert.Equal(2, all.TopTargets.Count);
        Assert.Equal("button#submit-btn", all.TopTargets[0].Target);
        Assert.Equal(2, all.TopTargets[0].Count);
        Assert.Equal("checkout", all.TopTargets[1].Target);
        Assert.Equal(1, all.TopTargets[1].Count);
    }

    /// <summary>窗口边界、分页、筛选与跨项目隔离.</summary>
    [Fact]
    public async Task Sessions_SearchAndReplay_AreWindowAndProjectScoped()
    {
        const string events = """
            [
              {"type":"pageview","ts":"2026-11-15T12:00:00Z","extra":{"ua":"Browser","domain":"demo.local"}},
              {"type":"error","ts":"2026-11-15T12:00:01Z","name":"boom","pagePath":"/cart"},
              {"type":"vital","ts":"2026-11-15T12:00:02Z","name":"LCP","value":100},
              {"type":"click","ts":"2026-11-15T12:00:03Z","pagePath":"/cart","extra":{"x":10,"y":10,"pageW":100,"pageH":100}},
              {"type":"replay-chunk","ts":"2026-11-15T12:00:04Z","seq":1,"blobBase64":"W10="},
              {"type":"error","ts":"2026-11-15T12:01:00Z","name":"outside","pagePath":"/outside"},
              {"type":"vital","ts":"2026-11-15T12:01:00Z","name":"LCP","value":999}
            ]
            """;
        await IngestAsync("rum_demo", "same", events);
        await IngestAsync("other", "same", events);
        await IngestAsync("rum_demo", "second", """
            [{"type":"pageview","ts":"2026-11-15T12:00:00Z"}]
            """);
        var from = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
        var to = from.AddMinutes(1);
        var page = await _rumStore.SearchSessionsAsync("rum_demo", from, to, new RumSessionFilter { PageSize = 1 });
        Assert.Equal(2, page.Count);
        Assert.Single(page.Items);
        Assert.Equal("same", page.Items[0].SessionId);
        Assert.Equal(1, page.Items[0].ErrorCount);
        Assert.Equal(1, page.Items[0].PageViews);
        Assert.True(page.Items[0].HasReplay);
        Assert.Equal(from.AddSeconds(4), page.Items[0].LastSeen);
        var second = await _rumStore.SearchSessionsAsync("rum_demo", from, to, new RumSessionFilter { PageSize = 1, Page = 2 });
        Assert.Equal("second", Assert.Single(second.Items).SessionId);
        var filtered = await _rumStore.SearchSessionsAsync("rum_demo", from, to, new RumSessionFilter { Search = "/cart", HasErrors = true, HasReplay = true });
        Assert.Equal(1, filtered.Count);
        Assert.Empty((await _rumStore.SearchSessionsAsync("rum_demo", from, to, new RumSessionFilter { Search = "' OR 1=1 --" })).Items);
        Assert.Single(await _rumStore.GetReplayAsync("rum_demo", "same", from, to));
        Assert.Empty(await _rumStore.GetReplayAsync("missing", "same", from, to));
        Assert.Equal(2, (await _rumStore.ListSessionEventsAsync("rum_demo", "same", from, from.AddSeconds(3))).Count);
        Assert.Single(await _rumStore.AggregateErrorsAsync("rum_demo", from, to, 10));
        Assert.Equal(100, (await _rumStore.AggregatePerformanceAsync("rum_demo", from, to)).VitalP75["LCP"]);
        Assert.Single(await _rumStore.AggregatePageTreeAsync("rum_demo", from, to, 10));
        Assert.Equal(1, (await _rumStore.AggregateHeatmapAsync("rum_demo", from, to, 20)).TotalClicks);
    }

    private async Task IngestAsync(string projectKey, string sessionId, string eventsJson)
    {
        var json = $$"""
            {"projectKey": "{{projectKey}}", "sessionId": "{{sessionId}}", "events": {{eventsJson}}}
            """;
        var status = await _beaconService.IngestAsync(System.Text.Encoding.UTF8.GetBytes(json), null, "https://intranet.local");
        Assert.Equal(RumIngestStatus.Ok, status);
        await _writer.FlushAsync(DrainAll(), CancellationToken.None);
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

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            _connectionManager.DisposeAsync().GetAwaiter().GetResult();
        }
        finally
        {
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch (IOException)
            {
                // Windows 文件句柄释放延迟，不阻塞测试
            }
        }
    }
}
