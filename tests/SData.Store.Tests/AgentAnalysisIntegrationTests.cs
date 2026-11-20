using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Agents;
using SData.Infra.Exceptions;
using SData.Store.Core;
using SData.Store.Models;
using Xunit;

namespace SData.Store.Tests;

public partial class StoreIntegrationTests
{
    [Fact]
    public async Task AgentAnalysis_DiscoversQueriesAndAnalyzesAllDomains()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
        {
            _buffer.TryEnqueueLog(new LogRecord { Timestamp = now.AddSeconds(i), Service = "api", Severity = "ERROR", Body = "password=secret", TraceId = "trace" });
            _buffer.TryEnqueueMetric(new MetricPointRecord { Timestamp = now.AddSeconds(i), Name = "cpu", Value = 10 + i, LabelsJson = """{"host.name":"a","password":"not-exposed"}""" });
            _buffer.TryEnqueueMetric(new MetricPointRecord { Timestamp = now.AddSeconds(i), Name = "cpu", Value = 20 + i, LabelsJson = """{"host.name":"b"}""" });
            _buffer.TryEnqueueTelemetry(new TelemetryRecord { Timestamp = now.AddSeconds(i), DeviceKey = "sensor", Signal = "temperature", NumValue = 30 + i, Quality = i % 2 });
            _buffer.TryEnqueueRumSession(new RumSessionRecord { Timestamp = now.AddSeconds(i), ProjectKey = "web", SessionId = "session", Domain = "example.test" });
        }
        _buffer.TryEnqueueLog(new LogRecord { Timestamp = now.AddHours(-1), Service = "api", Body = "baseline" });
        _buffer.TryEnqueueSpan(new SpanRecord { Timestamp = now, Service = "api", TraceId = "trace", SpanId = "parent", DurationUs = 10000 });
        for (var i = 0; i < 2; i++)
            _buffer.TryEnqueueSpan(new SpanRecord { Timestamp = now, Service = "db", TraceId = "trace", SpanId = "child", ParentSpanId = "parent", DurationUs = 2000, Status = "error" });
        _buffer.TryEnqueueRumEvent(new RumEventRecord { Timestamp = now, ProjectKey = "web", SessionId = "session", EventType = "vital", EventName = "LCP", Value = 100, PayloadJson = """{"secret":"hidden"}""" });
        _buffer.TryEnqueueRumEvent(new RumEventRecord { Timestamp = now, ProjectKey = "web", SessionId = "session", EventType = "error", EventName = "TypeError", PagePath = "/home" });
        _buffer.TryEnqueueRumEvent(new RumEventRecord { Timestamp = now, ProjectKey = "other", SessionId = "other", EventType = "error", EventName = "excluded" });
        await DrainAsync();
        var analysis = Analysis();
        var from = now.AddMinutes(-1); var to = now.AddMinutes(1);
        AgentRequest Window() => new() { From = from, To = to };
        foreach (var tool in new[] { "list_data_sources", "get_data_organization", "summarize_logs", "summarize_traces", "get_service_dependencies", "list_devices", "list_rum_projects" })
            Assert.NotNull(await analysis.ExecuteAsync(tool, Window(), default));
        var tables = await analysis.ExecuteAsync("list_tables", new AgentTableQuery { Limit = 2 }, default);
        Assert.Equal(2, tables.Rows.Count);
        Assert.True(tables.Truncated);
        var schema = await analysis.ExecuteAsync("describe_table", new AgentTableQuery { Table = "logs_20261120" }, default);
        Assert.Contains("TIMESTAMP", JsonSerializer.Serialize(schema.Metadata));
        Assert.Contains("Untrusted log text", JsonSerializer.Serialize(schema.Metadata));
        var rows = await analysis.ExecuteAsync("query_data", new AgentDataQuery { From = from, To = to, Domain = "logs", Fields = ["body"], Filters = [new() { Field = "service", Value = JsonSerializer.SerializeToElement("api") }] }, default);
        Assert.Equal(3, rows.Rows.Count);
        Assert.DoesNotContain("password=secret", JsonSerializer.Serialize(rows));
        var aggregate = await analysis.ExecuteAsync("aggregate_data", new AgentDataQuery { From = from, To = to }, default);
        Assert.Equal(3, Convert.ToInt64(aggregate.Rows.Single().Single()));
        var compared = await analysis.ExecuteAsync("compare_periods", new AgentCompareQuery { From = from, To = to, BaselineFrom = from.AddHours(-1), BaselineTo = to.AddHours(-1) }, default);
        Assert.Equal(200, Convert.ToDouble(compared.Rows.Single()[4]));
        var zero = await analysis.ExecuteAsync("compare_periods", new AgentCompareQuery { From = from, To = to, BaselineFrom = from.AddHours(-2), BaselineTo = to.AddHours(-2) }, default);
        Assert.Null(zero.Rows.Single()[4]);
        var values = await analysis.ExecuteAsync("get_field_values", new AgentFieldQuery { From = from, To = to, Field = "severity" }, default);
        Assert.Equal("ERROR", values.Rows.Single()[0]);
        var dependencies = await analysis.ExecuteAsync("get_service_dependencies", Window(), default);
        Assert.Equal(1, Convert.ToInt64(dependencies.Rows.Single()[2]));
        var labels = await analysis.ExecuteAsync("list_metric_labels", new AgentSeriesQuery { From = from, To = to, Metric = "cpu" }, default);
        Assert.Equal(2, labels.Rows.Count);
        Assert.DoesNotContain("not-exposed", JsonSerializer.Serialize(labels));
        var series = await analysis.ExecuteAsync("query_metric_series", new AgentSeriesQuery { From = from, To = to, Metric = "cpu", SplitByLabels = true }, default);
        Assert.Equal(2, series.Rows.Count);
        var filtered = await analysis.ExecuteAsync("query_metric_series", new AgentSeriesQuery { From = from, To = to, Metric = "cpu", Labels = new() { ["host.name"] = "a" } }, default);
        Assert.Equal(11, Convert.ToDouble(filtered.Rows.Single()[1]));
        var signals = await analysis.ExecuteAsync("list_device_signals", new AgentSeriesQuery { From = from, To = to, Device = "sensor" }, default);
        Assert.Equal("temperature", signals.Rows.Single()[0]);
        var iot = await analysis.ExecuteAsync("query_iot_series", new AgentSeriesQuery { From = from, To = to, Device = "sensor", Signal = "temperature" }, default);
        Assert.Equal(31, Convert.ToDouble(iot.Rows.Single()[1]));
        Assert.Equal(2, iot.Sections["quality"].Rows.Count);
        var rum = await analysis.ExecuteAsync("summarize_rum", new AgentRumQuery { From = from, To = to, Project = "web" }, default);
        Assert.Equal(3, Convert.ToInt64(rum.Sections["page_reports"].Rows.Single()[0]));
        Assert.Equal(1, Convert.ToInt64(rum.Sections["page_reports"].Rows.Single()[1]));
        Assert.Equal(100, Convert.ToDouble(rum.Sections["vitals"].Rows.Single()[2]));
        var errors = await analysis.ExecuteAsync("summarize_rum_errors", new AgentRumQuery { From = from, To = to, Project = "web" }, default);
        Assert.Equal("TypeError", errors.Rows.Single()[0]);
        Assert.DoesNotContain("excluded", JsonSerializer.Serialize(errors));
        Assert.Equal("unknown", errors.Completeness);
        var legacy = new AgentQueries(new AnalyticsExecutor(_connectionManager, _systemOptions, Resolver, NullLogger<AnalyticsExecutor>.Instance), NullLogger<AgentQueries>.Instance, _systemOptions, analysis);
        var services = Assert.IsType<AgentResult>(await legacy.ExecuteAsync("list_services", new AgentQuery { From = from, To = to }, default));
        Assert.Contains(services.Sections["traces"].Rows, row => (string?)row[0] == "db");
    }

    [Fact]
    public async Task AgentAnalysis_RejectsUnsafeInputsAndBudgetsOutput()
    {
        var analysis = Analysis();
        foreach (var query in new AgentRequest[]
        {
            new AgentDataQuery { Domain = "sqlite_master" },
            new AgentDataQuery { Fields = ["attributes"] },
            new AgentDataQuery { Fields = ["body; DROP TABLE user"] },
            new AgentDataQuery { Filters = [new() { Field = "service", Op = "sql", Value = JsonSerializer.SerializeToElement("x") }] },
            new AgentDataQuery { Domain = "sessions" },
            new AgentDataQuery { Filters = [new() { Field = "service" }] },
            new AgentDataQuery { Limit = 201 },
            new AgentDataQuery { From = DateTimeOffset.UtcNow.AddDays(-2) },
        })
        {
            var error = await Assert.ThrowsAsync<BusinessException>(() => analysis.ExecuteAsync("query_data", query, default));
            Assert.Equal(400, error.StatusCode);
        }
        foreach (var table in new[] { "sdata_info", "agent_keys", "logs_archive_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_20261120", "logs_20261120;SELECT 1" })
            Assert.Equal(400, (await Assert.ThrowsAsync<BusinessException>(() => analysis.ExecuteAsync("describe_table", new AgentTableQuery { Table = table }, default))).StatusCode);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 200; i++)
            _buffer.TryEnqueueLog(new LogRecord { Timestamp = now, Service = "budget", Body = new string('x', 3000) });
        await DrainAsync();
        var result = await analysis.ExecuteAsync("query_data", new AgentDataQuery { Fields = ["body"], Limit = 200, From = now.AddMinutes(-1), To = now.AddMinutes(1) }, default);
        Assert.True(result.Truncated);
        Assert.True(result.FieldTruncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result)) <= 96 * 1024);
        var hostile = "'; DROP TABLE logs; --";
        var safe = await analysis.ExecuteAsync("query_data", new AgentDataQuery { Filters = [new() { Field = "service", Value = JsonSerializer.SerializeToElement(hostile) }] }, default);
        Assert.Empty(safe.Rows);
    }

    [Fact]
    public async Task AgentAnalysis_QueriesColdFamiliesAcrossUtcMidnight()
    {
        var start = new DateTimeOffset(2026, 11, 19, 23, 59, 0, TimeSpan.Zero);
        foreach (var timestamp in new[] { start, start.AddMinutes(2) })
        {
            _buffer.TryEnqueueLog(new LogRecord { Timestamp = timestamp, Service = "cold" });
            _buffer.TryEnqueueSpan(new SpanRecord { Timestamp = timestamp, TraceId = "cold", SpanId = timestamp.ToString("O"), Service = "cold" });
            _buffer.TryEnqueueMetric(new MetricPointRecord { Timestamp = timestamp, Name = "cold", Value = 1 });
            _buffer.TryEnqueueTelemetry(new TelemetryRecord { Timestamp = timestamp, DeviceKey = "cold", Signal = "cold", NumValue = 1 });
            _buffer.TryEnqueueRumSession(new RumSessionRecord { Timestamp = timestamp, ProjectKey = "cold", SessionId = "cold" });
            _buffer.TryEnqueueRumEvent(new RumEventRecord { Timestamp = timestamp, ProjectKey = "cold", SessionId = "cold", EventType = "error", EventName = "cold" });
        }
        await DrainAsync();
        foreach (var prefix in new[] { TimeTables.LogsPrefix, TimeTables.TracesPrefix, TimeTables.MetricsPrefix, TimeTables.IotPrefix, TimeTables.RumSessionPrefix, TimeTables.RumEventPrefix })
        {
            var table = TimeTables.Name(prefix, start);
            var directory = Path.Combine(_tempDirectory, "data", "cold", prefix.TrimEnd('_'));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "20261119.parquet").Replace('\\', '/');
            await _connectionManager.ExecuteWriteAsync($"COPY {table} TO '{path.Replace("'", "''")}' (FORMAT PARQUET)");
            await _connectionManager.DropTableAsync(table);
        }
        var analysis = Analysis();
        foreach (var domain in new[] { "logs", "traces", "metrics", "iot", "sessions", "rum_events" })
        {
            var result = await analysis.ExecuteAsync("aggregate_data", new AgentDataQuery { Domain = domain, Project = "cold", From = start, To = start.AddMinutes(3) }, default);
            Assert.Equal(2, Convert.ToInt64(result.Rows.Single().Single()));
        }
        var tables = await analysis.ExecuteAsync("list_tables", new AgentTableQuery { Domain = "rum_events" }, default);
        Assert.Single(tables.Rows);
        Assert.Equal("rum_event_20261120", tables.Rows.Single()[1]);
        Assert.NotNull(tables.Rows.Single()[3]);
    }

    [Fact]
    public async Task AgentAnalysis_RealHttpMcpCallsUseDuckDbAndRejectUnsafeRequests()
    {
        var now = DateTimeOffset.UtcNow;
        _buffer.TryEnqueueLog(new LogRecord { Timestamp = now, Service = "http-mcp", Severity = "ERROR", Body = "Bearer private-token" });
        await DrainAsync();
        var file = Path.Combine(_tempDirectory, "protocol.db");
        var key = "sda_" + new string('b', 64);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<DatabaseContext>(o => o.UseSqlite("Data Source=" + file));
        builder.Services.AddSingleton<AgentRequestBudget>();
        builder.Services.AddSingleton<IAgentAnalysis>(Analysis());
        builder.Services.AddSingleton<IAgentQueries>(provider => new AgentQueries(
            new AnalyticsExecutor(_connectionManager, _systemOptions, Resolver, NullLogger<AnalyticsExecutor>.Instance),
            NullLogger<AgentQueries>.Instance, _systemOptions, provider.GetRequiredService<IAgentAnalysis>()));
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<AgentTools>();
        await using var app = builder.Build();
        app.Use((context, next) => context.RequestServices.GetRequiredService<AgentRequestBudget>().InvokeAsync(context, next));
        app.MapMcp("/api/mcp");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            await db.Database.EnsureCreatedAsync();
            db.AgentKeys.Add(new AgentKeyEntity { Id = 100, Name = "http", Prefix = key[..12], KeyHash = HashHelper.Sha256Hex(key) });
            await db.SaveChangesAsync();
        }
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
            client.DefaultRequestHeaders.Authorization = new("Bearer", key);
            async Task<JsonElement> Rpc(string method, object? parameters)
            {
                using var response = await client.PostAsJsonAsync("/api/mcp", new { jsonrpc = "2.0", id = 1, method, @params = parameters });
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.IsSuccessStatusCode, body);
                var payload = body.Split('\n').FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal))?[5..].Trim() ?? body;
                return JsonDocument.Parse(payload).RootElement.Clone();
            }
            await Rpc("initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "integration", version = "1" } });
            var tools = await Rpc("tools/list", null);
            Assert.Equal(26, tools.GetProperty("result").GetProperty("tools").GetArrayLength());
            var data = await Rpc("tools/call", new { name = "query_data", arguments = new { query = new { domain = "logs", from = now.AddMinutes(-1), to = now.AddMinutes(1) } } });
            var text = data.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Contains("http-mcp", text);
            Assert.DoesNotContain("private-token", text);
            var bad = await Rpc("tools/call", new { name = "query_data", arguments = new { query = new { fields = new[] { "attributes" } } } });
            Assert.True(bad.GetProperty("result").GetProperty("isError").GetBoolean());
            var aggregate = await Rpc("tools/call", new { name = "aggregate_data", arguments = new { query = new { domain = "logs", from = now.AddMinutes(-1), to = now.AddMinutes(1) } } });
            Assert.False(aggregate.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean());
            var tables = await Rpc("tools/call", new { name = "list_tables", arguments = new { query = new { domain = "logs" } } });
            Assert.Contains("logs_", tables.GetRawText());
        }
        finally
        {
            await app.StopAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private AgentAnalysis Analysis() => new(new AnalyticsExecutor(_connectionManager, _systemOptions, Resolver, NullLogger<AnalyticsExecutor>.Instance),
        new AnalyticsCatalog(_connectionManager), NullLogger<AgentAnalysis>.Instance, _systemOptions);
}
