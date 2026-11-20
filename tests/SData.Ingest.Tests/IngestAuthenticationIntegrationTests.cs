using System.Net;
using System.Net.Http.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using SData.Ingest.Api;
using SData.Ingest.OtlpGrpc;
using SData.Ingest.Services;
using SData.Infra.Configuration;
using SData.Infra.Api.Conventions;
using Xunit;

namespace SData.Ingest.Tests;

public class IngestAuthenticationIntegrationTests
{
    [Fact]
    public async Task Http_AllServerRoutes_RequireKey_EvenWithLegacyDisableSwitch()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new SystemOptions { Ingest = { RequireAuth = false } });
        builder.Services.AddSingleton<IIngestKeyValidator, Validator>();
        builder.Services.AddScoped<IngestApiKeyFilter>();
        builder.Services.AddSingleton<IOtlpParser, OtlpParser>();
        builder.Services.AddSingleton<IOtlpProtoParser, OtlpProtoParserService>();
        builder.Services.AddSingleton<IEsBulkParser, EsBulkParserService>();
        builder.Services.AddSingleton<IIngestIngestor, EmptyIngestor>();
        builder.Services.AddControllers(o => o.Conventions.Add(new ApiRouteConvention("api")))
            .AddApplicationPart(typeof(IngestController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };
        foreach (var path in new[] { "/v1/logs", "/v1/traces", "/v1/metrics", "/api/v1/logs",
            "/api/ingest/otlp/v1/logs", "/api/ingest/logs", "/api/ingest/traces",
            "/api/ingest/metrics", "/api/ingest/iot", "/_bulk", "/test/_bulk" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, new { })).StatusCode);
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add("X-Api-Key", "valid-ingest");
            request.Content = new StringContent(path.Contains("_bulk") ? "" : path.Contains("/ingest/") && !path.Contains("otlp") ? "[]" : "{}", System.Text.Encoding.UTF8, "application/json");
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task Grpc_ThreeSignals_RejectMissingKey_AndAcceptValidMetadata()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(o => o.Interceptors.Add<IngestAuthenticationInterceptor>());
        builder.Services.AddSingleton<IIngestKeyValidator, Validator>();
        builder.Services.AddSingleton<IIngestIngestor, EmptyIngestor>();
        builder.Services.AddSingleton<IOtlpProtoParser, OtlpProtoParserService>();
        await using var app = builder.Build();
        app.MapGrpcService<OtlpGrpcLogsService>();
        app.MapGrpcService<OtlpGrpcTracesService>();
        app.MapGrpcService<OtlpGrpcMetricsService>();
        await app.StartAsync();
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(Address(app));
        var logs = new LogsService.LogsServiceClient(channel);
        var traces = new TraceService.TraceServiceClient(channel);
        var metrics = new MetricsService.MetricsServiceClient(channel);
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await logs.ExportAsync(new ExportLogsServiceRequest()))).StatusCode);
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await traces.ExportAsync(new ExportTraceServiceRequest()))).StatusCode);
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await metrics.ExportAsync(new ExportMetricsServiceRequest()))).StatusCode);
        var headers = new Metadata { { "x-api-key", "valid-ingest" } };
        await logs.ExportAsync(new ExportLogsServiceRequest(), headers);
        await traces.ExportAsync(new ExportTraceServiceRequest(), headers);
        await metrics.ExportAsync(new ExportMetricsServiceRequest(), headers);
        await app.StopAsync();
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private class Validator : IIngestKeyValidator
    {
        public Task<bool> ValidateAsync(string apiKey) => Task.FromResult(apiKey == "valid-ingest");
    }
    private class EmptyIngestor : IIngestIngestor
    {
        public bool EnqueueLog(SData.Store.Models.LogRecord record) => true;
        public bool EnqueueSpan(SData.Store.Models.SpanRecord record) => true;
        public bool EnqueueMetric(SData.Store.Models.MetricPointRecord record) => true;
        public bool EnqueueTelemetry(SData.Store.Models.TelemetryRecord record) => true;
        public IngestResponse IngestLogs(IEnumerable<IngestLogItem> items) => new();
        public IngestResponse IngestSpans(IEnumerable<IngestSpanItem> items) => new();
        public IngestResponse IngestMetrics(IEnumerable<IngestMetricItem> items) => new();
        public IngestResponse IngestTelemetry(IEnumerable<IngestTelemetryItem> items) => new();
    }
}
