using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SData.Settings.Services;
using SData.Infra;
using SData.Infra.Services;
using SData.Settings;
using SData.Store;
using SData.Store.Core;
using SData.Store.Models;
using Xunit;

namespace SData.Ingest.Tests;

/// <summary>
/// 固定本地遥测不依赖外部出口.
/// </summary>
public class SelfTelemetryTests
{
    [Theory]
    [InlineData("", "", "grpc")]
    [InlineData("invalid", "file:///tmp/metrics", "grpc")]
    [InlineData("", "", "invalid")]
    [InlineData("http://127.0.0.1:1", "", "http/protobuf")]
    [InlineData("", "http://127.0.0.1:1", "http/protobuf")]
    public void Module_AlwaysExportsLocally(string trace, string metrics, string protocol)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SData:Database:Path"] = $"self-telemetry-missing-{Guid.NewGuid():N}.db",
            ["SData:OTLP:Trace"] = trace,
            ["SData:OTLP:Metrics"] = metrics,
            ["SData:OTLP:Protocol"] = protocol,
        }).Build();
        var buffer = new RecordingBuffer();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IIngestBuffer>(buffer);
        new ConfigureOpenTelemetryModule(configuration).ConfigureServices(new Maomi.DefaultServiceContext(services, configuration));
        using var provider = services.BuildServiceProvider();
        var tracer = provider.GetRequiredService<TracerProvider>();
        var meterProvider = provider.GetRequiredService<MeterProvider>();
        using var source = new ActivitySource(AppConst.ActivitySourceName);
        using (var activity = source.StartActivity("self-telemetry-test"))
        {
            Assert.NotNull(activity);
        }

        using var meter = new Meter(AppConst.ActivitySourceName);
        meter.CreateCounter<long>("self.test.count").Add(3);
        // 外部不可达会使聚合刷新结果失败，本地出口仍须独立完成写入。
        tracer.ForceFlush();
        meterProvider.ForceFlush();
        Assert.Contains(buffer.Spans, span => span.Name == "self-telemetry-test");
        Assert.Contains(buffer.Metrics, point => point.Name == "self.test.count" && point.Value == 3);
    }

    [Fact]
    public void Trace_PreservesIdentityMicrosecondsEventsAndError()
    {
        var buffer = new RecordingBuffer();
        using var exporter = new LocalTraceExporter(buffer);
        using var tracer = Sdk.CreateTracerProviderBuilder().AddSource("local-trace-test")
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        using var source = new ActivitySource("local-trace-test");
        using var parent = source.StartActivity("parent");
        using (var activity = source.StartActivity("child", ActivityKind.Server))
        {
            Assert.NotNull(activity);
            activity.SetTag("error", true);
            activity.SetTag("test.number", 42);
            activity.AddEvent(new ActivityEvent("exception"));
            activity.SetEndTime(activity.StartTimeUtc.AddTicks(12340));
        }

        var child = Assert.Single(buffer.Spans, span => span.Name == "child");
        Assert.Equal(parent!.TraceId.ToHexString(), child.TraceId);
        Assert.Equal(parent.SpanId.ToHexString(), child.ParentSpanId);
        Assert.Equal(1234, child.DurationUs);
        Assert.Equal("server", child.Kind);
        Assert.Equal("error", child.Status);
        Assert.Contains("exception", child.EventsJson);
        Assert.Contains("test.number", child.AttributesJson);
        Assert.Contains("service.name", child.ResourceJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Metrics_MapsNumericTypesAndHistogramWithoutBlocking(bool full)
    {
        var buffer = new RecordingBuffer { Full = full };
        using var reader = new PeriodicExportingMetricReader(new LocalMetricExporter(buffer), int.MaxValue);
        using var provider = Sdk.CreateMeterProviderBuilder().AddMeter("local-metric-test").AddReader(reader).Build();
        using var meter = new Meter("local-metric-test");
        meter.CreateCounter<long>("long.sum").Add(3, new KeyValuePair<string, object?>("region", "local"));
        meter.CreateCounter<double>("double.sum").Add(2.5);
        meter.CreateObservableGauge("long.gauge", () => 7L);
        meter.CreateObservableGauge("double.gauge", () => 1.5);
        meter.CreateHistogram<double>("duration").Record(4);
        Assert.True(provider.ForceFlush());
        if (full)
        {
            Assert.Empty(buffer.Metrics);
            Assert.True(buffer.Attempts >= 6);
            return;
        }

        Assert.Contains(buffer.Metrics, point => point.Name == "long.sum" && point.Kind == "sum" && point.Value == 3 && point.LabelsJson!.Contains("\"region\":\"local\"", StringComparison.Ordinal));
        Assert.Contains(buffer.Metrics, point => point.Name == "double.sum" && point.Value == 2.5);
        Assert.Contains(buffer.Metrics, point => point.Name == "long.gauge" && point.Value == 7);
        Assert.Contains(buffer.Metrics, point => point.Name == "double.gauge" && point.Value == 1.5);
        Assert.Contains(buffer.Metrics, point => point.Name == "duration.count" && point.Value == 1);
        Assert.Contains(buffer.Metrics, point => point.Name == "duration.sum" && point.Value == 4);
        Assert.All(buffer.Metrics, point => Assert.Contains("sdata", point.LabelsJson));
    }

    [Theory]
    [InlineData("/v1/traces", true)]
    [InlineData("/api/v1/metrics", true)]
    [InlineData("/api/ingest/otlp/v1/logs", true)]
    [InlineData("/opentelemetry.proto.collector.trace.v1.TraceService/Export", true)]
    [InlineData("/opentelemetry.proto.collector.metrics.v1.MetricsService/Export", true)]
    [InlineData("/opentelemetry.proto.collector.logs.v1.LogsService/Export", true)]
    [InlineData("/api/settings", false)]
    public void ReceivingTelemetry_IsExcludedFromTracing(string path, bool excluded)
    {
        Assert.Equal(excluded, ConfigureOpenTelemetryModule.IsOtlpRequest(path));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("http://collector:4317", true)]
    [InlineData(" https://collector:4318/v1/traces ", true)]
    [InlineData("file:///tmp/export", false)]
    [InlineData("collector:4317", false)]
    public void ExternalSettings_ValidateOptionalHttpEndpoints(string endpoint, bool valid)
    {
        var validator = new AutoValidator<SaveOtlpSettingsCommand>();
        var result = validator.Validate(new SaveOtlpSettingsCommand { Trace = endpoint, Metrics = endpoint });
        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public async Task LocalExporters_WriteThroughBufferToDuckDb()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdata-self-telemetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new SData.Infra.Configuration.SystemOptions();
        options.DuckDb.Path = Path.Combine(directory, "self.duckdb");
        options.DuckDb.Threads = 2;
        options.Ingest.FlushIntervalMs = 10;
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<DuckDbConnectionManager>.Instance;
        await using var connection = new DuckDbConnectionManager(options, logger);
        await connection.InitializeAsync();
        var buffer = new IngestBuffer(options);
        using var writer = new IngestWriterService(buffer, connection, SystemSettingsProvider.FileOnly(options), new DefaultIdProvider(1),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IngestWriterService>.Instance);
        await writer.StartAsync(CancellationToken.None);
        using var tracer = Sdk.CreateTracerProviderBuilder().AddSource("local-store-test")
            .AddProcessor(new BatchActivityExportProcessor(new LocalTraceExporter(buffer))).Build();
        using var metrics = Sdk.CreateMeterProviderBuilder().AddMeter("local-store-test")
            .AddReader(new PeriodicExportingMetricReader(new LocalMetricExporter(buffer), int.MaxValue)).Build();
        using var source = new ActivitySource("local-store-test");
        string traceId;
        using (var activity = source.StartActivity("local-round-trip"))
        {
            Assert.NotNull(activity);
            traceId = activity.TraceId.ToHexString();
        }

        using var meter = new Meter("local-store-test");
        meter.CreateCounter<long>("self.round.trip").Add(9);
        Assert.True(tracer.ForceFlush());
        Assert.True(metrics.ForceFlush());
        var traceStore = new TraceStore(connection, SystemSettingsProvider.FileOnly(options));
        var metricStore = new MetricStore(connection, options);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var persisted = false;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (buffer.GetStats().Processed.GetValueOrDefault("trace") > 0 && buffer.GetStats().Processed.GetValueOrDefault("metric") > 0)
                {
                    var spans = await traceStore.GetTraceAsync(traceId);
                    var span = Assert.Single(spans);
                    Assert.Equal(AppConst.ServiceName, span.Service);
                    var result = await metricStore.QueryRangeAsync(new MetricSeriesQuery
                    {
                        Name = "self.round.trip",
                        Start = DateTimeOffset.UtcNow.AddMinutes(-1),
                        End = DateTimeOffset.UtcNow.AddMinutes(1),
                        StepSeconds = 60,
                    });
                    Assert.Contains(result.SeriesList, series => series.Points.Any(point => point.Value == 9));
                    persisted = true;
                    break;
                }

                await Task.Delay(20);
            }

            Assert.True(persisted, "自身链路和指标应经真实缓冲写入 DuckDB");
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("http/protobuf", 200, false)]
    [InlineData("http/protobuf", 401, false)]
    [InlineData("http/protobuf", 403, false)]
    [InlineData("grpc", 200, false)]
    [InlineData("http/protobuf", 200, true)]
    [InlineData("grpc", 200, true)]
    public async Task ExternalOtlp_ReceivesBothSignalsAlongsideLocalExportWithoutRecursion(string protocol, int status, bool emptyHeaders)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var endpoint = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen =>
            listen.Protocols = protocol == "grpc" ? Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2 : Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SData:Database:Path"] = $"self-telemetry-missing-{Guid.NewGuid():N}.db",
            ["SData:OTLP:Trace"] = protocol == "grpc" ? endpoint : endpoint + "/v1/traces",
            ["SData:OTLP:Metrics"] = protocol == "grpc" ? endpoint : endpoint + "/v1/metrics",
            ["SData:OTLP:Protocol"] = protocol,
            ["SData:OTLP:TraceHeaders"] = emptyHeaders ? "" : "Authorization=Bearer trace-token,x-tenant=trace,x-encoded=a%2Cb%25",
            ["SData:OTLP:MetricsHeaders"] = emptyHeaders ? "" : "Authorization=Basic dGVzdA==,x-tenant=metrics",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "authorization=Bearer environment-secret",
            ["OTEL_EXPORTER_OTLP_TRACES_HEADERS"] = "authorization=Bearer environment-trace",
            ["OTEL_EXPORTER_OTLP_METRICS_HEADERS"] = "authorization=Bearer environment-metrics",
        });
        var buffer = new RecordingBuffer();
        builder.Services.AddSingleton<IIngestBuffer>(buffer);
        new ConfigureOpenTelemetryModule(builder.Configuration).ConfigureServices(
            new Maomi.DefaultServiceContext(builder.Services, builder.Configuration));
        var receivedHeaders = new ConcurrentDictionary<string, string>();
        var receivedTraces = new ConcurrentBag<ExportTraceServiceRequest>();
        var receivedMetrics = new ConcurrentBag<ExportMetricsServiceRequest>();
        await using var app = builder.Build();
        app.MapPost(protocol == "grpc" ? "/opentelemetry.proto.collector.trace.v1.TraceService/Export" : "/v1/traces", async context =>
        {
            receivedHeaders["trace"] = context.Request.Headers.Authorization.ToString();
            receivedHeaders["trace-tenant"] = context.Request.Headers["x-tenant"].ToString();
            receivedHeaders["encoded"] = context.Request.Headers["x-encoded"].ToString();
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            receivedTraces.Add(ExportTraceServiceRequest.Parser.ParseFrom(body.ToArray().AsSpan(protocol == "grpc" ? 5 : 0).ToArray()));
            await RespondAsync(context, protocol, status);
        });
        app.MapPost(protocol == "grpc" ? "/opentelemetry.proto.collector.metrics.v1.MetricsService/Export" : "/v1/metrics", async context =>
        {
            receivedHeaders["metrics"] = context.Request.Headers.Authorization.ToString();
            receivedHeaders["metrics-tenant"] = context.Request.Headers["x-tenant"].ToString();
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            receivedMetrics.Add(ExportMetricsServiceRequest.Parser.ParseFrom(body.ToArray().AsSpan(protocol == "grpc" ? 5 : 0).ToArray()));
            await RespondAsync(context, protocol, status);
        });
        await app.StartAsync();
        try
        {
            using var source = new ActivitySource(AppConst.ActivitySourceName);
            using (var activity = source.StartActivity("dual-export-test"))
            {
                Assert.NotNull(activity);
            }

            using var meter = new Meter(AppConst.ActivitySourceName);
            meter.CreateCounter<long>("self.dual.count").Add(11);
            var tracer = app.Services.GetRequiredService<TracerProvider>();
            var metrics = app.Services.GetRequiredService<MeterProvider>();
            tracer.ForceFlush();
            metrics.ForceFlush();
            Assert.Contains(buffer.Spans, span => span.Name == "dual-export-test");
            Assert.Contains(buffer.Metrics, point => point.Name == "self.dual.count" && point.Value == 11);
            Assert.Contains(receivedTraces, request => request.ResourceSpans
                .SelectMany(resource => resource.ScopeSpans).SelectMany(scope => scope.Spans)
                .Any(span => span.Name == "dual-export-test"));
            Assert.Contains(receivedMetrics, request => request.ResourceMetrics
                .SelectMany(resource => resource.ScopeMetrics).SelectMany(scope => scope.Metrics)
                .Any(metric => metric.Name == "self.dual.count"));
            Assert.Equal(emptyHeaders ? "" : "Bearer trace-token", receivedHeaders["trace"]);
            Assert.Equal(emptyHeaders ? "" : "Basic dGVzdA==", receivedHeaders["metrics"]);
            Assert.Equal(emptyHeaders ? "" : "trace", receivedHeaders["trace-tenant"]);
            Assert.Equal(emptyHeaders ? "" : "a,b%", receivedHeaders["encoded"]);
            Assert.Equal(emptyHeaders ? "" : "metrics", receivedHeaders["metrics-tenant"]);
            var count = buffer.Spans.Count;
            Assert.True(tracer.ForceFlush());
            Assert.Equal(count, buffer.Spans.Count);
            Assert.DoesNotContain(buffer.Spans, span => span.Name.Contains("/v1/", StringComparison.Ordinal));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData("authorization=Bearer token,x-api-key=abc==", true)]
    [InlineData("x-key=a%2Cb%25", true)]
    [InlineData("x-key=a%0D%0Ab", false)]
    [InlineData("x-key=a%00b", false)]
    [InlineData("authorization=a,Authorization=b", false)]
    [InlineData("x-key=a\r\nx-other=b", false)]
    [InlineData("\n", false)]
    [InlineData("grpc-timeout=10", false)]
    [InlineData("x-key-bin=abc", false)]
    [InlineData("content-type=text/plain", false)]
    [InlineData("missing-value", false)]
    public void Headers_ValidateSafeMetadata(string headers, bool valid)
    {
        Assert.Equal(valid, SData.Infra.Configuration.OtlpHeaders.IsValid(headers));
        var validator = new AutoValidator<SaveOtlpSettingsCommand>();
        Assert.Equal(valid, validator.Validate(new SaveOtlpSettingsCommand { TraceHeaders = headers }).IsValid);
    }

    private static async Task RespondAsync(HttpContext context, string protocol, int status)
    {
        context.Response.StatusCode = status;
        if (protocol == "grpc")
        {
            context.Response.ContentType = "application/grpc";
            context.Response.DeclareTrailer("grpc-status");
            await context.Response.Body.WriteAsync(new byte[5]);
            context.Response.AppendTrailer("grpc-status", "0");
        }
        else
        {
            context.Response.ContentType = "application/x-protobuf";
        }
    }

    private sealed class RecordingBuffer : IIngestBuffer
    {
        public bool Full { get; init; }
        public int Attempts { get; private set; }
        public ConcurrentBag<SpanRecord> Spans { get; } = new();
        public ConcurrentBag<MetricPointRecord> Metrics { get; } = new();
        public bool TryEnqueueSpan(SpanRecord record)
        {
            Attempts++;
            if (!Full)
            {
                Spans.Add(record);
            }

            return !Full;
        }

        public bool TryEnqueueMetric(MetricPointRecord record)
        {
            Attempts++;
            if (!Full)
            {
                Metrics.Add(record);
            }

            return !Full;
        }

        public bool TryEnqueueLog(LogRecord record) => !Full;
        public bool TryEnqueueTelemetry(TelemetryRecord record) => !Full;
        public bool TryEnqueueRumEvent(RumEventRecord record) => !Full;
        public bool TryEnqueueRumSession(RumSessionRecord record) => !Full;
        public bool TryEnqueueRumReplay(RumReplayRecord record) => !Full;
        public IngestBufferStats GetStats() => new();
    }
}
