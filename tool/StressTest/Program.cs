// SData 压测工具：写入压力（ingest）+ 查询压力（query/mixed）。
// 用法示例：
//   dotnet run -c Release -- --scenario ingest --duration 120 --concurrency 16 --batch 500 --domain logs
//   dotnet run -c Release -- --scenario query  --duration 120 --concurrency 8
//   dotnet run -c Release -- --scenario mixed  --duration 180 --concurrency 16 --query-concurrency 8
// 鉴权：接入走 X-Api-Key（--key 或 SDATA_INGESTION_KEY）；查询走 JWT（--user/--password 登录获取）。

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var ConsoleEncoding = new UTF8Encoding(false);
try { Console.OutputEncoding = ConsoleEncoding; } catch { /* 管道下可能失败 */ }

var opt = Options.Parse(args);
if (opt == null)
{
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("[warn] 收到 Ctrl+C，正在收尾并输出已采集的统计 ...");
    cts.Cancel();
};

var http = new HttpClient(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    MaxConnectionsPerServer = opt.IngestConcurrency + opt.QueryConcurrency + 8,
    UseProxy = false,
})
{
    BaseAddress = new Uri(opt.Server.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(60),
};
http.DefaultRequestHeaders.Add("X-Api-Key", opt.IngestKey);
http.DefaultRequestHeaders.ConnectionClose = false;

string? jwt = null;
if (opt.Scenario is Scenario.query or Scenario.mixed or Scenario.status)
{
    jwt = await LoginAsync(opt);
    if (jwt == null)
    {
        return 3;
    }
}

// 预热：建立连接池，避免首个请求统计失真
await WarmupAsync(opt, jwt);

var swTotal = Stopwatch.StartNew();
var ingest = new Metrics("ingest");
var query = new Metrics("query");

Console.WriteLine($"目标: {opt.Server}  场景: {opt.Scenario}  时长: {opt.Duration}s");
Console.WriteLine($"ingest 并发={opt.IngestConcurrency} 批大小={opt.BatchSize} 域={string.Join(',', opt.Domains)} | query 并发={opt.QueryConcurrency} 档位={string.Join(',', opt.Profiles)}");
Console.WriteLine(new string('-', 100));

using var printCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
var printTask = Task.Run(() => ProgressLoop(printCts.Token));

var tasks = new List<Task>();
if (opt.Scenario is Scenario.ingest or Scenario.mixed)
{
    for (var i = 0; i < opt.IngestConcurrency; i++)
    {
        var workerId = i;
        tasks.Add(Task.Run(() => IngestWorker(opt, ingest, cts.Token, workerId), cts.Token));
    }
}

if (opt.Scenario is Scenario.query or Scenario.mixed)
{
    var profiles = QueryProfiles.Build(opt).ToList();
    for (var i = 0; i < opt.QueryConcurrency; i++)
    {
        var workerId = i;
        tasks.Add(Task.Run(() => QueryWorker(opt, profiles, query, cts.Token, workerId, jwt!), cts.Token));
    }
}

var deadline = TimeSpan.FromSeconds(opt.Duration);
try
{
    await Task.WhenAll(tasks);
}
catch (OperationCanceledException)
{
    // Ctrl+C：任务内部响应取消
}

var wall = swTotal.Elapsed;
printCts.Cancel();
try { await printTask; } catch { /* 忽略 */ }

Console.WriteLine(new string('-', 100));
if (opt.Scenario is Scenario.ingest or Scenario.mixed)
{
    ingest.Print(wall, opt);
}

if (opt.Scenario is Scenario.query or Scenario.mixed)
{
    query.Print(wall, opt);
}

if (opt.OutFile is { } outFile)
{
    File.WriteAllText(outFile, SummaryJson.Build(opt, wall, ingest, query), ConsoleEncoding);
    Console.WriteLine($"摘要已写入 {outFile}");
}

return 0;

// ============================== workers ==============================

async Task IngestWorker(Options opt, Metrics m, CancellationToken ct, int workerId)
{
    var gen = new DataGenerator(workerId);
    var domains = opt.Domains;
    var stopwatch = Stopwatch.StartNew();
    var deadline = TimeSpan.FromSeconds(opt.Duration);

    try
    {
        var i = 0;
        while (stopwatch.Elapsed < deadline && !ct.IsCancellationRequested)
        {
            var domain = domains[(workerId + i) % domains.Length];
            var payload = gen.BuildBatch(domain, opt.BatchSize);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts.Default);
            var path = domain switch
            {
                "logs" => "api/ingest/logs",
                "traces" => "api/ingest/traces",
                "metrics" => "api/ingest/metrics",
                _ => "api/ingest/iot",
            };
            using var content = new ByteArrayContent(bytes, 0, bytes.Length);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var sw = Stopwatch.StartNew();
            int status;
            try
            {
                using var resp = await http.PostAsync(path, content, ct);
                status = (int)resp.StatusCode;
                if (status >= 500 && m.SampleError())
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    Console.WriteLine($"[err] {path} -> {status} {Trunc(body, 200)}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                status = -1;
                if (m.SampleError())
                {
                    Console.WriteLine($"[err] {path} -> exception {ex.GetType().Name}: {Trunc(ex.Message, 160)}");
                }
            }

            sw.Stop();
            m.Record(domain, sw.ElapsedTicks, status, batchItems: payload.Count, bytes: bytes.Length);
            i++;
        }
    }
    catch (OperationCanceledException)
    {
    }
}

async Task QueryWorker(Options opt, List<QueryProfile> profiles, Metrics m, CancellationToken ct, int workerId, string jwt)
{
    var stopwatch = Stopwatch.StartNew();
    var deadline = TimeSpan.FromSeconds(opt.Duration);

    try
    {
        var i = 0;
        while (stopwatch.Elapsed < deadline && !ct.IsCancellationRequested)
        {
            var profile = profiles[(workerId + i) % profiles.Count];
            var body = profile.BuildBody();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOpts.Default);
            using var req = new HttpRequestMessage(new HttpMethod(profile.Method), profile.Path)
            {
                Content = new ByteArrayContent(bytes, 0, bytes.Length),
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

            var sw = Stopwatch.StartNew();
            long? serverTook = null;
            int status;
            try
            {
                using var resp = await http.SendAsync(req, ct);
                status = (int)resp.StatusCode;
                var respBytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (status >= 400 && m.SampleError())
                {
                    Console.WriteLine($"[err] {profile.Name} -> {status} {Trunc(Encoding.UTF8.GetString(respBytes), 240)}");
                }

                serverTook = ExtractTook(respBytes);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                status = -1;
                if (m.SampleError())
                {
                    Console.WriteLine($"[err] {profile.Name} -> exception {ex.GetType().Name}: {Trunc(ex.Message, 160)}");
                }
            }

            sw.Stop();
            m.Record(profile.Name, sw.ElapsedTicks, status, batchItems: 1, bytes: bytes.Length, tookMs: serverTook);
            i++;
        }
    }
    catch (OperationCanceledException)
    {
    }
}

async Task ProgressLoop(CancellationToken ct)
{
    long lastIngest = 0, lastQuery = 0;
    long lastItems = 0, lastBytes = 0;
    var sw = Stopwatch.StartNew();
    try
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            var di = ingest.Requests - lastIngest;
            var dq = query.Requests - lastQuery;
            var dItems = ingest.Items - lastItems;
            var dMb = (ingest.Bytes - lastBytes) / 1048576.0;
            lastIngest = ingest.Requests;
            lastQuery = query.Requests;
            lastItems = ingest.Items;
            lastBytes = ingest.Bytes;
            var parts = new List<string> { $"[t={sw.Elapsed.TotalSeconds,5:F0}s]" };
            if (opt.Scenario is Scenario.ingest or Scenario.mixed)
            {
                parts.Add($"ingest {di}/s  items {dItems}/s  {dMb:F1}MB/s  429={ingest.Status429}  err={ingest.Fail}");
            }

            if (opt.Scenario is Scenario.query or Scenario.mixed)
            {
                parts.Add($"query {dq}/s  err={query.Fail}");
            }

            Console.WriteLine(string.Join("  |  ", parts));
        }
    }
    catch (OperationCanceledException)
    {
    }
}

async Task WarmupAsync(Options opt, string? jwt)
{
    try
    {
        using var warm = new HttpRequestMessage(HttpMethod.Get, "api/health");
        if (jwt != null)
        {
            warm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        }

        using var resp = await http.SendAsync(warm);
    }
    catch
    {
        Console.WriteLine("[warn] 预热请求失败（服务可能未启动）");
    }
}

async Task<string?> LoginAsync(Options opt)
{
    try
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { username = opt.User, password = opt.Password }, JsonOpts.Default);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var resp = await http.PostAsync("api/auth/login", content);
        var text = await resp.Content.ReadAsStringAsync();
        if ((int)resp.StatusCode != 200)
        {
            Console.WriteLine($"[fatal] 登录失败 HTTP {(int)resp.StatusCode}: {Trunc(text, 300)}");
            return null;
        }

        using var doc = JsonDocument.Parse(text);
        var token = doc.RootElement.GetProperty("token").GetString();
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("[fatal] 登录响应缺少 token");
            return null;
        }

        Console.WriteLine($"登录成功: {opt.User}");
        return token;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[fatal] 登录异常: {ex.Message}");
        return null;
    }
}

static long? ExtractTook(byte[] body)
{
    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var (name, mul) in new[] { ("took", 1L), ("elapsedMs", 1L), ("tookMs", 1L) })
        {
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms))
            {
                return ms * mul;
            }
        }
    }
    catch
    {
        // 非 JSON 响应（如健康检查），忽略
    }

    return null;
}

static string Trunc(string s, int n) => s.Length <= n ? s.ReplaceLineEndings(" ") : s[..n].ReplaceLineEndings(" ") + "...";

// ============================== metrics ==============================

sealed class Metrics(string name)
{
    private readonly ConcurrentQueue<long> _latencies = new();
    private long _ok, _status429, _fail;
    private int _errorSamples;
    private readonly ConcurrentDictionary<string, LabelStats> _labels = new();

    public long Requests;
    public long Status429;
    public long Fail;
    public long Items;
    public long Bytes;

    public void Record(string label, long latTicks, int status, long batchItems, long bytes, long? tookMs = null)
    {
        Interlocked.Increment(ref Requests);
        Interlocked.Add(ref Items, batchItems);
        Interlocked.Add(ref Bytes, bytes);
        _latencies.Enqueue(latTicks);
        _labels.GetOrAdd(label, _ => new LabelStats()).Add(latTicks, status, tookMs);

        switch (status)
        {
            case 200:
                Interlocked.Increment(ref _ok);
                break;
            case 429:
                Interlocked.Increment(ref _status429);
                Interlocked.Increment(ref Status429);
                break;
            default:
                Interlocked.Increment(ref _fail);
                Interlocked.Increment(ref Fail);
                break;
        }
    }

    public bool SampleError() => Interlocked.Increment(ref _errorSamples) <= 10;

    public void Print(TimeSpan wall, Options opt)
    {
        var lat = _latencies.ToArray();
        Array.Sort(lat);
        var secs = Math.Max(wall.TotalSeconds, 0.001);
        Console.WriteLine($"== {name} 摘要 ({secs:F1}s) ==");
        Console.WriteLine($"  请求: {Requests}  成功: {_ok}  429: {_status429}  失败: {_fail}   RPS: {Requests / secs:F1}");
        if (name == "ingest")
        {
            Console.WriteLine($"  数据条数: {Items}  ({Items / secs:F0} 条/s)   请求体: {Bytes / 1048576.0:F1} MB ({Bytes / 1048576.0 / secs:F1} MB/s)");
        }

        Console.WriteLine($"  HTTP 延迟 ms: avg {LabelStats.Avg(lat):F1}  p50 {LabelStats.Pct(lat, 50):F1}  p90 {LabelStats.Pct(lat, 90):F1}  p95 {LabelStats.Pct(lat, 95):F1}  p99 {LabelStats.Pct(lat, 99):F1}  max {LabelStats.Max(lat):F1}");

        if (_labels.IsEmpty)
        {
            return;
        }

        Console.WriteLine($"  按{(name == "ingest" ? "域" : "查询档位")}细分:");
        Console.WriteLine($"    {"label",-14} {"reqs",8} {"rps",8} {"ok",8} {"429",8} {"fail",6}   {"lat p50",8} {"p90",8} {"p95",8} {"p99",8} ms   {"took p50",8} {"p95",8} ms");
        foreach (var (label, stats) in _labels.OrderBy(kv => kv.Key))
        {
            var (reqs, ok, e429, fail, latP, tookP) = stats.Summarize();
            Console.WriteLine(
                $"    {label,-14} {reqs,8} {reqs / secs,8:F1} {ok,8} {e429,8} {fail,6}   {latP[0],8:F1} {latP[1],8:F1} {latP[2],8:F1} {latP[3],8:F1}    {tookP[0],8:F1} {tookP[1],8:F1}");
        }
    }
}

sealed class LabelStats
{
    private readonly object _lock = new();
    private readonly List<long> _latTicks = new();
    private readonly List<long> _tookMs = new();
    private long _reqs, _ok, _e429, _fail;

    public void Add(long latTicks, int status, long? tookMs)
    {
        lock (_lock)
        {
            _reqs++;
            _latTicks.Add(latTicks);
            if (status == 200)
            {
                _ok++;
            }
            else if (status == 429)
            {
                _e429++;
            }
            else
            {
                _fail++;
            }

            if (tookMs.HasValue)
            {
                _tookMs.Add(tookMs.Value);
            }
        }
    }

    public (long reqs, long ok, long e429, long fail, double[] latPct, double[] tookPct) Summarize()
    {
        long[] lat;
        long[] took;
        long reqs, ok, e429, fail;
        lock (_lock)
        {
            lat = _latTicks.ToArray();
            took = _tookMs.ToArray();
            (reqs, ok, e429, fail) = (_reqs, _ok, _e429, _fail);
        }

        Array.Sort(lat);
        Array.Sort(took);
        return (reqs, ok, e429, fail, [Pct(lat, 50), Pct(lat, 90), Pct(lat, 95), Pct(lat, 99)], [took.Length == 0 ? 0 : Percent(took, 50), took.Length == 0 ? 0 : Percent(took, 95)]);
    }

    private static double Percent(long[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var i = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(i, 0, sorted.Length - 1)];
    }

    public static double Avg(long[] sorted) => sorted.Length == 0 ? 0 : sorted.Average() * 1000.0 / Stopwatch.Frequency;

    public static double Pct(long[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var i = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(i, 0, sorted.Length - 1)] * 1000.0 / Stopwatch.Frequency;
    }

    public static double Max(long[] sorted) => sorted.Length == 0 ? 0 : sorted[^1] * 1000.0 / Stopwatch.Frequency;
}

// ============================== 查询档位 ==============================

sealed class QueryProfile(string name, string method, string path, Func<object> buildBody)
{
    public string Name => name;
    public string Method => method;
    public string Path => path;
    public Func<object> BuildBody => buildBody;
}

static class QueryProfiles
{
    public static IEnumerable<QueryProfile> Build(Options opt)
    {
        var want = opt.Profiles;
        if (want.Contains("all") || want.Contains("logs-raw"))
        {
            yield return new QueryProfile("logs-raw", "POST", "api/logs/search", () => Dsl(
                minutes: 15,
                filters: [new Dictionary<string, object?> { ["field"] = "service", ["op"] = "eq", ["value"] = DataGenerator.Services[1] }],
                sort: new[] { new Dictionary<string, object?> { ["field"] = "ts", ["desc"] = true } },
                limit: 100));
        }

        if (want.Contains("all") || want.Contains("logs-hist"))
        {
            yield return new QueryProfile("logs-hist", "POST", "api/logs/search", () => Dsl(
                minutes: 60,
                bucket: new Dictionary<string, object?> { ["step"] = "1m", ["func"] = "count" }));
        }

        if (want.Contains("all") || want.Contains("logs-agg"))
        {
            yield return new QueryProfile("logs-agg", "POST", "api/logs/search", () => Dsl(
                minutes: 60,
                groupBy: ["service"],
                aggregations: [new Dictionary<string, object?> { ["func"] = "count", ["alias"] = "c" }]));
        }

        if (want.Contains("all") || want.Contains("traces-agg"))
        {
            yield return new QueryProfile("traces-agg", "POST", "api/traces/search", () => Dsl(
                minutes: 60,
                domain: "traces",
                groupBy: ["service"],
                aggregations:
                [
                    new Dictionary<string, object?> { ["func"] = "avg", ["field"] = "duration_us", ["alias"] = "avg_dur" },
                    new Dictionary<string, object?> { ["func"] = "quantile", ["field"] = "duration_us", ["q"] = 0.95, ["alias"] = "p95" },
                ]));
        }

        if (want.Contains("all") || want.Contains("logs-text"))
        {
            yield return new QueryProfile("logs-text", "POST", "api/logs/search", () => Dsl(
                minutes: 60,
                filters: [new Dictionary<string, object?> { ["field"] = "body", ["op"] = "match", ["value"] = "订单" }],
                limit: 100));
        }

        if (want.Contains("all") || want.Contains("logs-page"))
        {
            yield return new QueryProfile("logs-page", "GET", "api/logs", () => new Dictionary<string, object?>());
        }

        if (want.Contains("all") || want.Contains("analytics"))
        {
            yield return new QueryProfile("analytics-sql", "POST", "api/analytics/search", () => new Dictionary<string, object?>
            {
                ["sql"] = "SELECT service, severity, count(*) AS c FROM {{logs}} WHERE ts >= $start AND ts < $end GROUP BY service, severity ORDER BY c DESC",
                ["from"] = DateTimeOffset.UtcNow.AddMinutes(-60),
                ["to"] = DateTimeOffset.UtcNow,
                ["rowLimit"] = 100,
            });
        }
    }

    private static Dictionary<string, object?> Dsl(
        int minutes,
        object[]? filters = null,
        string[]? groupBy = null,
        object[]? aggregations = null,
        Dictionary<string, object?>? bucket = null,
        object[]? sort = null,
        int limit = 50,
        string domain = "logs")
    {
        var now = DateTimeOffset.UtcNow;
        var d = new Dictionary<string, object?>
        {
            ["range"] = new Dictionary<string, object?> { ["from"] = now.AddMinutes(-minutes), ["to"] = now },
            ["filters"] = filters ?? Array.Empty<object>(),
            ["groupBy"] = groupBy ?? Array.Empty<string>(),
            ["aggregations"] = aggregations ?? Array.Empty<object>(),
            ["bucket"] = bucket,
            ["sort"] = sort ?? Array.Empty<object>(),
            ["limit"] = limit,
        };
        return d;
    }
}

// ============================== 数据生成 ==============================

sealed class DataGenerator(int seed)
{
    public static readonly string[] Services = ["api-gateway", "order-service", "user-service", "payment-service", "inventory-service", "notification-service"];
    private static readonly string[] LogTemplates =
    [
        "订单 {order} 创建成功，用户 {user} 购买 SKU-{sku} x{qty}，金额 ¥{amt}，流水号 PAY{pay}，渠道 {channel}",
        "订单 {order} 支付回调处理完成，状态 {status}，耗时 {ms}ms，订单金额 ¥{amt}",
        "用户 {user} 登录成功，IP 192.168.{a}.{b}，UA Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/{v}",
        "库存扣减成功：SKU-{sku} 数量 {qty}，剩余 {stock}，仓库 WH-{wh}",
        "通知发送成功：用户 {user} 短信模板 SMS_{tpl}，运营商回复码 {code}",
        "缓存刷新完成：key inventory:{sku}，命中 {hit}%，TTL 重置 {ttl}s",
        "支付网关握手成功：商户 {merchant}，通道 {channel}，延迟 {ms}ms",
        "优惠券核销：用户 {user} 使用 CPN{cpn} 抵扣 ¥{amt}，订单 {order}",
    ];
    private static readonly string[] Warnings =
    [
        "订单 {order} 支付重试第 {retry} 次，网关返回超时，将退避 {backoff}ms 后重试",
        "库存服务响应缓慢：SKU-{sku} 查询 {ms}ms 超过阈值 300ms，rid {rid}",
        "Redis 连接池紧张：active {active}/max {max}，等待 {wait}ms",
        "MQ 积压告警：topic order.events lag {lag}，消费组 cg-order",
    ];
    private static readonly string[] ErrorHeads =
    [
        "订单 {order} 支付失败：网关返回 E{code}，金额 ¥{amt}，rid {rid}",
        "用户 {user} 下单失败：库存服务 500，SKU-{sku}，rid {rid}",
        "通知发送失败：用户 {user} 邮箱网关 503，模板 {tpl}，rid {rid}",
    ];
    private static readonly string[] SpanNames =
    [
        "POST /api/orders", "GET /api/orders/{id}", "GET /api/users/{id}", "POST /api/payments",
        "GET /api/sku/{sku}", "POST /api/notify", "SELECT orders", "SELECT inventory", "redis.get", "kafka.produce",
    ];
    private static readonly string[] MetricNames =
    [
        "sys.cpu.percent", "sys.memory.percent", "jvm.heap.used", "http.server.duration.ms",
        "db.connections.active", "redis.hit.rate", "mq.backlog",
    ];
    private static readonly string[] Channels = ["wechat", "alipay", "unionpay", "card"];
    private static readonly string[] IotSignals = ["temperature", "humidity", "vibration", "battery"];
    private static readonly string[] DoorStates = ["open", "closed"];

    private readonly Random _random = new(unchecked(seed * 7919 + Environment.TickCount));

    private double NextDouble(double min, double max) => min + (_random.NextDouble() * (max - min));

    private string Fill(string tpl)
    {
        return tpl
            .Replace("{order}", $"SO{NextLong(1_000_000_000, 9_999_999_999)}")
            .Replace("{user}", $"u_{_random.Next(100000, 999999)}")
            .Replace("{sku}", _random.Next(100000, 999999).ToString())
            .Replace("{qty}", _random.Next(1, 9).ToString())
            .Replace("{amt}", NextDouble(1, 9999).ToString("F2"))
            .Replace("{pay}", NextLong(100000000000L, 999999999999L).ToString())
            .Replace("{channel}", Channels[_random.Next(Channels.Length)])
            .Replace("{status}", _random.Next(10) < 8 ? "PAID" : "PENDING")
            .Replace("{ms}", _random.Next(3, 900).ToString())
            .Replace("{a}", _random.Next(1, 254).ToString())
            .Replace("{b}", _random.Next(1, 254).ToString())
            .Replace("{v}", _random.Next(120, 140) + ".0")
            .Replace("{stock}", _random.Next(1, 5000).ToString())
            .Replace("{wh}", _random.Next(1, 12).ToString())
            .Replace("{tpl}", _random.Next(100, 999).ToString())
            .Replace("{code}", _random.Next(1000, 9999).ToString())
            .Replace("{hit}", _random.Next(60, 100).ToString())
            .Replace("{ttl}", _random.Next(30, 900).ToString())
            .Replace("{merchant}", $"M{_random.Next(10000, 99999)}")
            .Replace("{cpn}", _random.Next(10000, 99999).ToString())
            .Replace("{retry}", _random.Next(1, 5).ToString())
            .Replace("{backoff}", (_random.Next(1, 8) * 250).ToString())
            .Replace("{rid}", $"r{NextLong(100000000, 999999999)}")
            .Replace("{active}", _random.Next(40, 96).ToString())
            .Replace("{max}", "96")
            .Replace("{wait}", _random.Next(5, 200).ToString())
            .Replace("{lag}", _random.Next(100, 90000).ToString());
    }

    private long NextLong(long min, long max) => min + (long)(_random.NextDouble() * (max - min));

    private string Severity()
    {
        var r = _random.Next(100);
        return r < 70 ? "INFO" : r < 82 ? "DEBUG" : r < 92 ? "WARN" : "ERROR";
    }

    private string BodyFor(string severity)
    {
        if (severity == "WARN")
        {
            return Fill(Warnings[_random.Next(Warnings.Length)]);
        }

        if (severity == "ERROR")
        {
            return Fill(ErrorHeads[_random.Next(ErrorHeads.Length)]) + "\n" + Stack();
        }

        return Fill(LogTemplates[_random.Next(LogTemplates.Length)]);
    }

    private string Stack()
    {
        var fam = _random.Next(3);
        var sb = new StringBuilder(1200);
        if (fam == 0)
        {
            sb.Append("java.lang.RuntimeException: payment gateway timeout\n\tat com.sdata.payment.GatewayClient.execute(GatewayClient.java:").Append(_random.Next(40, 300)).Append(")\n");
            for (var i = 0; i < 14; i++)
            {
                sb.Append("\tat com.sdata.order.service.OrderService.step").Append(i).Append("(OrderService.java:").Append(_random.Next(40, 900)).Append(")\n");
            }

            sb.Append("Caused by: java.net.SocketTimeoutException: Read timed out\n\tat java.net.SocketInputStream.socketRead0(Native Method)");
        }
        else if (fam == 1)
        {
            sb.Append("System.TimeoutException: The operation has timed out.\n   at SData.Payment.GatewayClient.Execute(String orderNo)\n");
            for (var i = 0; i < 14; i++)
            {
                sb.Append("   at SData.Order.Services.OrderService.Step").Append(i).Append("(OrderService.cs:").Append(_random.Next(40, 900)).Append(")\n");
            }

            sb.Append("   --- End of inner exception stack trace --- ---> System.IO.IOException: Unable to read data");
        }
        else
        {
            sb.Append("panic: runtime error: index out of range [").Append(_random.Next(1, 99)).Append("] with length 0\n\ngoroutine ").Append(_random.Next(1000, 99999)).Append(" [running]:\n");
            for (var i = 0; i < 10; i++)
            {
                sb.Append("github.com/sdata/order/internal/service.(*OrderService).Step").Append(i).Append("(0x").Append(NextLong(0xc000000000, 0xc000ffffff).ToString("x")).Append(")\n");
            }

            sb.Append("exit status 2");
        }

        return sb.ToString();
    }

    public List<object> BuildBatch(string domain, int size)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var list = new List<object>(size);
        switch (domain)
        {
            case "logs":
                for (var i = 0; i < size; i++)
                {
                    var svc = Services[_random.Next(Services.Length)];
                    var sev = Severity();
                    list.Add(new IngestLogDto
                    {
                        TimestampMs = nowMs,
                        Service = svc,
                        Severity = sev,
                        Body = BodyFor(sev),
                        TraceId = null,
                        SpanId = null,
                        Attributes = new Dictionary<string, object?> { ["rid"] = $"r{NextLong(100000000, 999999999)}", ["thread"] = $"http-nio-{_random.Next(1, 32)}" },
                        Resource = new Dictionary<string, object?> { ["service.name"] = svc, ["host.name"] = $"host-{_random.Next(1, 4)}", ["deployment.environment"] = "stress" },
                    });
                }

                break;
            case "traces":
                for (var i = 0; i < size; i++)
                {
                    var svc = Services[_random.Next(Services.Length)];
                    list.Add(new IngestSpanDto
                    {
                        TraceId = NextLong(0x1000000000000000L, 0x7fffffffffffffffL).ToString("x16"),
                        SpanId = NextLong(0x100000000L, 0xffffffffL).ToString("x8"),
                        ParentSpanId = null,
                        TimestampMs = nowMs,
                        DurationUs = (long)NextDouble(300, 900_000),
                        Service = svc,
                        Name = SpanNames[_random.Next(SpanNames.Length)],
                        Kind = _random.Next(10) < 6 ? "server" : "client",
                        Status = _random.Next(20) == 0 ? "error" : "ok",
                        Attributes = new Dictionary<string, object?> { ["http.method"] = "POST", ["http.status_code"] = 200, ["rid"] = $"r{NextLong(100000000, 999999999)}" },
                        Resource = new Dictionary<string, object?> { ["service.name"] = svc, ["deployment.environment"] = "stress" },
                    });
                }

                break;
            case "metrics":
                for (var i = 0; i < size; i++)
                {
                    var svc = Services[_random.Next(Services.Length)];
                    list.Add(new IngestMetricDto
                    {
                        Name = MetricNames[_random.Next(MetricNames.Length)],
                        TimestampMs = nowMs,
                        Value = NextDouble(0, 100),
                        Labels = new Dictionary<string, object?> { ["service"] = svc, ["route"] = SpanNames[_random.Next(6)], ["host"] = $"host-{_random.Next(1, 4)}" },
                        Kind = "gauge",
                    });
                }

                break;
            default:
                for (var i = 0; i < size; i++)
                {
                    var sig = IotSignals[_random.Next(IotSignals.Length)];
                    var isDoor = _random.Next(10) == 0;
                    list.Add(new IngestTelemetryDto
                    {
                        DeviceKey = $"dev-{_random.Next(1, 7):D3}",
                        Signal = isDoor ? "door" : sig,
                        TimestampMs = nowMs,
                        NumValue = isDoor ? null : NextDouble(10, 90),
                        StrValue = isDoor ? DoorStates[_random.Next(2)] : null,
                        Quality = 0,
                    });
                }

                break;
        }

        return list;
    }
}

// ============================== DTO ==============================

sealed class IngestLogDto
{
    [JsonPropertyName("timestampMs")] public long? TimestampMs { get; set; }
    [JsonPropertyName("service")] public string Service { get; set; } = string.Empty;
    [JsonPropertyName("severity")] public string? Severity { get; set; }
    [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
    [JsonPropertyName("traceId")] public string? TraceId { get; set; }
    [JsonPropertyName("spanId")] public string? SpanId { get; set; }
    [JsonPropertyName("attributes")] public Dictionary<string, object?>? Attributes { get; set; }
    [JsonPropertyName("resource")] public Dictionary<string, object?>? Resource { get; set; }
}

sealed class IngestSpanDto
{
    [JsonPropertyName("traceId")] public string TraceId { get; set; } = string.Empty;
    [JsonPropertyName("spanId")] public string SpanId { get; set; } = string.Empty;
    [JsonPropertyName("parentSpanId")] public string? ParentSpanId { get; set; }
    [JsonPropertyName("timestampMs")] public long? TimestampMs { get; set; }
    [JsonPropertyName("durationUs")] public long DurationUs { get; set; }
    [JsonPropertyName("service")] public string Service { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("attributes")] public Dictionary<string, object?>? Attributes { get; set; }
    [JsonPropertyName("resource")] public Dictionary<string, object?>? Resource { get; set; }
}

sealed class IngestMetricDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("timestampMs")] public long? TimestampMs { get; set; }
    [JsonPropertyName("value")] public double Value { get; set; }
    [JsonPropertyName("labels")] public Dictionary<string, object?>? Labels { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
}

sealed class IngestTelemetryDto
{
    [JsonPropertyName("deviceKey")] public string DeviceKey { get; set; } = string.Empty;
    [JsonPropertyName("signal")] public string Signal { get; set; } = string.Empty;
    [JsonPropertyName("timestampMs")] public long? TimestampMs { get; set; }
    [JsonPropertyName("numValue")] public double? NumValue { get; set; }
    [JsonPropertyName("strValue")] public string? StrValue { get; set; }
    [JsonPropertyName("quality")] public int Quality { get; set; }
}

// ============================== options & summary ==============================

enum Scenario { ingest, query, mixed, status }

static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}

sealed class Options
{
    public string Server { get; set; } = "http://localhost:5300";
    public string IngestKey { get; set; } = string.Empty;
    public Scenario Scenario { get; set; } = Scenario.ingest;
    public int IngestConcurrency { get; set; } = 16;
    public int QueryConcurrency { get; set; } = 8;
    public int BatchSize { get; set; } = 500;
    public int Duration { get; set; } = 60;
    public string[] Domains { get; set; } = ["logs"];
    public string[] Profiles { get; set; } = ["all"];
    public string User { get; set; } = "admin";
    public string Password { get; set; } = "sdata@123";
    public string? OutFile { get; set; }
    public string? MonitorOut { get; set; }

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        o.IngestKey = Environment.GetEnvironmentVariable("SDATA_INGESTION_KEY") ?? string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"参数 {a} 缺值");
            switch (a)
            {
                case "--server": o.Server = Next(); break;
                case "--key": o.IngestKey = Next(); break;
                case "--scenario": o.Scenario = Enum.Parse<Scenario>(Next(), ignoreCase: true); break;
                case "--concurrency": o.IngestConcurrency = int.Parse(Next()); break;
                case "--query-concurrency": o.QueryConcurrency = int.Parse(Next()); break;
                case "--batch": o.BatchSize = int.Parse(Next()); break;
                case "--duration": o.Duration = int.Parse(Next()); break;
                case "--domain": o.Domains = Next().Split(','); break;
                case "--profile": o.Profiles = Next().Split(','); break;
                case "--user": o.User = Next(); break;
                case "--password": o.Password = Next(); break;
                case "--out": o.OutFile = Next(); break;
                case "--monitor-out": o.MonitorOut = Next(); break;
                case "-h": case "--help": PrintHelp(); return null;
                default: Console.WriteLine($"未知参数: {a}"); PrintHelp(); return null;
            }
        }

        if (string.IsNullOrEmpty(o.IngestKey) && o.Scenario is Scenario.ingest or Scenario.mixed)
        {
            Console.WriteLine("[fatal] 缺少接入 Key：--key 或环境变量 SDATA_INGESTION_KEY");
            return null;
        }

        if (o.Scenario == Scenario.status)
        {
            o.Duration = 0;
        }

        return o;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            SData 压测工具
              --server URL            服务地址（默认 http://localhost:5300）
              --key KEY               接入 Key（或 SDATA_INGESTION_KEY）
              --scenario S            ingest | query | mixed | status
              --concurrency N         写入并发数（默认 16）
              --query-concurrency N   查询并发数（默认 8）
              --batch N               每批条数（默认 500）
              --duration S            持续秒数（默认 60）
              --domain LIST           域逗号分隔：logs,traces,metrics,iot
              --profile LIST          查询档位逗号分隔：logs-raw,logs-hist,logs-agg,traces-agg,logs-text,logs-page,analytics,all
              --user / --password     查询登录凭据（默认 admin / sdata@123）
              --out FILE              摘要 JSON 输出
              --monitor-out FILE      服务 monitor/status 快照输出
            """);
    }
}

static class SummaryJson
{
    public static string Build(Options opt, TimeSpan wall, Metrics ingest, Metrics query)
    {
        var snap = new
        {
            server = opt.Server,
            scenario = opt.Scenario.ToString(),
            wallSeconds = Math.Round(wall.TotalSeconds, 1),
            ingest = new { requests = ingest.Requests, accepted429 = ingest.Status429, failed = ingest.Fail, items = ingest.Items, bytes = ingest.Bytes, itemsPerSecond = Math.Round(ingest.Items / Math.Max(wall.TotalSeconds, 0.001), 0) },
            query = new { requests = query.Requests, failed = query.Fail, requestsPerSecond = Math.Round(query.Requests / Math.Max(wall.TotalSeconds, 0.001), 1) },
        };
        return JsonSerializer.Serialize(snap, JsonOpts.Default);
    }
}
