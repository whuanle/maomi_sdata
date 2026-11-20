using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SData.Rum;
using SData.Rum.Api;
using SData.Rum.Api.Controllers;
using SData.Rum.Hubs;
using SData.Rum.Models;
using SData.Rum.Services;
using SData.Infra.Configuration;
using SData.Store;
using SData.Store.Core;
using SData.Store.Models;
using Xunit;

namespace SData.Ingest.Tests;

public class RumCorsIntegrationTests
{
    [Fact]
    public async Task CorsAndWebSocketEnforceSourceWithoutOpeningManagementApis()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddCors();
        builder.Services.AddSingleton<ICorsPolicyProvider, RumCorsPolicyProvider>();
        builder.Services.AddSingleton<IRumOriginPolicy, TestPolicy>();
        var buffer = new IngestBuffer(new SystemOptions());
        builder.Services.AddSingleton<IIngestBuffer>(buffer);
        builder.Services.AddSingleton<IRumBeaconService, RumBeaconService>();
        var publisher = new Publisher();
        builder.Services.AddSingleton<IRumLivePublisher>(publisher);
        builder.Services.AddSignalR();
        builder.Services.AddControllers().AddApplicationPart(typeof(RumBeaconController).Assembly);
        await using var app = builder.Build();
        app.UseCors();
        // 模拟认证后的上下文，不开放真实服务的认证旁路。
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers["X-Test-Authenticated"] == "yes")
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test")], "test"));
            await next(context);
        });
        app.UseMiddleware<RumHubOriginMiddleware>();
        app.MapControllers();
        app.MapHub<RumLiveHub>("/hub/rum").RequireCors(ConfigureSignalRModule.RumCorsPolicy);
        app.MapGet("/api/test-management", () => "private");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        foreach (var path in new[] { "/rum/v1/config?projectKey=rum_demo", "/rum/v1/beacon", "/hub/rum/negotiate?projectKey=rum_demo&negotiateVersion=1" })
        {
            using var preflight = new HttpRequestMessage(HttpMethod.Options, path);
            preflight.Headers.Add("Origin", "https://allowed.test");
            preflight.Headers.Add("Access-Control-Request-Method", path.Contains("config") ? "GET" : "POST");
            preflight.Headers.Add("Access-Control-Request-Headers", "content-type,x-requested-with,x-signalr-user-agent");
            using var response = await client.SendAsync(preflight);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal("https://allowed.test", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        }

        using (var preflight = new HttpRequestMessage(HttpMethod.Options, "/rum/v1/beacon"))
        {
            preflight.Headers.Add("Origin", "https://evil.test");
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            using var response = await client.SendAsync(preflight);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }

        using (var management = new HttpRequestMessage(HttpMethod.Get, "/api/test-management"))
        {
            management.Headers.Add("Origin", "https://allowed.test");
            using var response = await client.SendAsync(management);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }

        foreach (var origin in new[] { "https://evil.test", "null", "" })
        {
            using var request = BeaconRequest(origin);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(buffer.Reader.TryRead(out _));
            Assert.Equal(0, publisher.Count);
        }

        using (var request = BeaconRequest("https://allowed.test"))
        {
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.True(buffer.Reader.TryRead(out _));
            Assert.Equal(1, publisher.Count);
        }

        // CORS 允许某项目的站点，不代表可以写入另一项目。
        using (var request = BeaconRequest("https://allowed.test", "rum_other"))
        {
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(buffer.Reader.TryRead(out _));
        }

        foreach (var origin in new[] { "https://allowed.test", "https://evil.test" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/hub/rum/negotiate?projectKey=rum_demo&negotiateVersion=1");
            request.Headers.Add("Origin", origin);
            using var response = await client.SendAsync(request);
            Assert.Equal(origin.Contains("evil") ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
        }

        var wsBase = new UriBuilder(client.BaseAddress!) { Scheme = "ws" }.Uri;
        using (var denied = new ClientWebSocket())
        {
            denied.Options.SetRequestHeader("Origin", "https://evil.test");
            await Assert.ThrowsAsync<WebSocketException>(() => denied.ConnectAsync(new Uri(wsBase, "/hub/rum?projectKey=rum_demo"), CancellationToken.None));
        }
        using (var socket = new ClientWebSocket())
        {
            socket.Options.SetRequestHeader("Origin", "https://allowed.test");
            await socket.ConnectAsync(new Uri(wsBase, "/hub/rum?projectKey=rum_demo"), CancellationToken.None);
            Assert.Equal(WebSocketState.Open, socket.State);
            socket.Abort();
        }
        using (var socket = new ClientWebSocket())
        {
            socket.Options.SetRequestHeader("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            socket.Options.SetRequestHeader("X-Test-Authenticated", "yes");
            await socket.ConnectAsync(new Uri(wsBase, "/hub/rum"), CancellationToken.None);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, CancellationToken.None);
            var bytes = new byte[4096];
            await socket.ReceiveAsync(bytes, CancellationToken.None);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":1,\"invocationId\":\"1\",\"target\":\"Subscribe\",\"arguments\":[\"rum_demo\"]}\u001e"), WebSocketMessageType.Text, true, CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reply = await socket.ReceiveAsync(bytes, timeout.Token);
            Assert.Contains("\"invocationId\":\"1\"", Encoding.UTF8.GetString(bytes, 0, reply.Count));
            Assert.DoesNotContain("\"error\"", Encoding.UTF8.GetString(bytes, 0, reply.Count));
            socket.Abort();
        }
        await app.StopAsync();
    }

    private static HttpRequestMessage BeaconRequest(string origin, string project = "rum_demo")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/rum/v1/beacon")
        {
            Content = new StringContent($$"""{"projectKey":"{{project}}","sessionId":"test","events":[{"type":"click"}]}""", Encoding.UTF8, "application/json"),
        };
        if (origin.Length > 0) request.Headers.Add("Origin", origin);
        return request;
    }

    private sealed class TestPolicy : IRumOriginPolicy
    {
        public bool Allows(string projectKey, string? origin) => projectKey == "rum_demo" && origin == "https://allowed.test";
        public bool AllowsAny(string? origin) => origin == "https://allowed.test";
    }

    private sealed class Publisher : IRumLivePublisher
    {
        public int Count { get; private set; }
        public bool PublishClick(RumEventRecord record, RumLiveClickEvent click) { Count++; return true; }
    }
}
