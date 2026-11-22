using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SData.Agents;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Services;
using SData.Settings.Services;
using Xunit;

namespace SData.Store.Tests;

public class AgentProtocolTests
{
    [Fact]
    public async Task Mcp_HandshakeToolsAndRevocation_UseIndependentCredential()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var key = "sda_" + new string('a', 64);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<DatabaseContext>(o => o.UseSqlite("Data Source=" + file));
        builder.Services.AddSingleton<SData.Settings.ISystemSettingsProvider>(SystemSettingsProvider.FileOnly(new SData.Infra.Configuration.SystemOptions()));
        builder.Services.AddSingleton<AgentRequestBudget>();
        builder.Services.AddSingleton<IAgentQueries, StubQueries>();
        builder.Services.AddSingleton<IAgentAnalysis, StubAnalysis>();
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<AgentTools>();
        await using var app = builder.Build();
        app.Use((context, next) => context.RequestServices.GetRequiredService<AgentRequestBudget>().InvokeAsync(context, next));
        app.MapMcp("/api/mcp");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            await db.Database.EnsureCreatedAsync();
            db.AgentKeys.Add(new AgentKeyEntity { Id = 100, Name = "test", Prefix = key[..12], KeyHash = HashHelper.Sha256Hex(key) });
            await db.SaveChangesAsync();
        }
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
            var initialize = new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "test", version = "1" } } };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/mcp", initialize)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "sdi_" + new string('a', 64));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/mcp", initialize)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", key);
            client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/mcp", initialize)).StatusCode);
            client.DefaultRequestHeaders.Remove("Origin");
            // 服务监听所有网卡，MCP 不做 Host 白名单；鉴权由 Agent Key 与同源 Origin 负责
            client.DefaultRequestHeaders.Host = "any.example";
            Assert.True((await client.PostAsJsonAsync("/api/mcp", initialize)).IsSuccessStatusCode);
            client.DefaultRequestHeaders.Host = null;
            var initialized = await client.PostAsJsonAsync("/api/mcp", initialize);
            Assert.True(initialized.IsSuccessStatusCode, await initialized.Content.ReadAsStringAsync());
            var list = await client.PostAsJsonAsync("/api/mcp", new { jsonrpc = "2.0", id = 2, method = "tools/list" });
            var body = await list.Content.ReadAsStringAsync();
            Assert.True(list.IsSuccessStatusCode, body);
            var payload = body.Split('\n').FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal))?[5..].Trim() ?? body;
            using var listed = JsonDocument.Parse(payload);
            var tools = listed.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
            Assert.Equal(26, tools.Length);
            var expected = typeof(AgentTools).GetMethods().Select(method => method.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).Cast<ModelContextProtocol.Server.McpServerToolAttribute>().SingleOrDefault()?.Name).Where(name => name != null).Order().ToArray();
            Assert.Equal(expected, tools.Select(t => t.GetProperty("name").GetString()).Order().ToArray());
            Assert.All(tools, t => Assert.True(t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()));
            var aggregateSchema = tools.Single(t => t.GetProperty("name").GetString() == "aggregate_data").GetProperty("inputSchema").GetRawText();
            Assert.Contains("aggregations", aggregateSchema);
            Assert.Contains("filters", aggregateSchema);
            Assert.Contains("domain", aggregateSchema);
            var analysisCall = await client.PostAsJsonAsync("/api/mcp", new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "aggregate_data", arguments = new { query = new { domain = "logs", limit = 10 } } } });
            Assert.Contains("analysis-protocol-ok", await analysisCall.Content.ReadAsStringAsync());
            var call = await client.PostAsJsonAsync("/api/mcp", new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "search_logs", arguments = new { query = new { limit = 10 } } } });
            Assert.Contains("protocol-ok", await call.Content.ReadAsStringAsync());
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
                (await db.AgentKeys.SingleAsync()).Enabled = false;
                await db.SaveChangesAsync();
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/mcp", initialize)).StatusCode);
        }
        finally
        {
            await app.StopAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Budget_RejectsConcurrentAndExcessRequests()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var key = "sda_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var services = new ServiceCollection();
        services.AddDbContext<DatabaseContext>(o => o.UseSqlite("Data Source=" + file));
        await using var provider = services.BuildServiceProvider();
        try
        {
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
                await db.Database.EnsureCreatedAsync();
                db.AgentKeys.Add(new AgentKeyEntity { Id = 100, Name = "budget", Prefix = key[..12], KeyHash = HashHelper.Sha256Hex(key) });
                await db.SaveChangesAsync();
            }
            var options = new SData.Infra.Configuration.SystemOptions();
            options.Agents.PerKeyConcurrency = 1;
            var budget = new AgentRequestBudget(SystemSettingsProvider.FileOnly(options));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var firstScope = provider.CreateScope();
            var first = Context(firstScope.ServiceProvider, key);
            var running = budget.InvokeAsync(first, async _ => { entered.SetResult(); await release.Task; });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                using var secondScope = provider.CreateScope();
                var second = Context(secondScope.ServiceProvider, key);
                await budget.InvokeAsync(second, _ => throw new InvalidOperationException("Rejected request reached handler"));
                Assert.Equal(429, second.Response.StatusCode);
            }
            finally { release.SetResult(); await running; }

            options.Agents.RequestsPerMinute = 1;
            budget = new AgentRequestBudget(SystemSettingsProvider.FileOnly(options));
            using var thirdScope = provider.CreateScope();
            await budget.InvokeAsync(Context(thirdScope.ServiceProvider, key), _ => Task.CompletedTask);
            using var fourthScope = provider.CreateScope();
            var fourth = Context(fourthScope.ServiceProvider, key);
            await budget.InvokeAsync(fourth, _ => throw new InvalidOperationException("Rate limited request reached handler"));
            Assert.Equal(429, fourth.Response.StatusCode);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(file); }
    }

    private static Microsoft.AspNetCore.Http.DefaultHttpContext Context(IServiceProvider services, string key)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = services };
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost");
        context.Request.Headers.Authorization = "Bearer " + key;
        return context;
    }

    private sealed class StubAnalysis : IAgentAnalysis
    {
        public Task<AgentResult> ExecuteAsync(string tool, AgentRequest query, CancellationToken cancellationToken)
            => Task.FromResult(new AgentResult { Semantics = "analysis-protocol-ok" });
    }

    private class StubQueries : IAgentQueries
    {
        public Task<object> ExecuteAsync(string tool, AgentQuery query, CancellationToken cancellationToken)
            => Task.FromResult<object>(new { message = "protocol-ok" });
    }
}
