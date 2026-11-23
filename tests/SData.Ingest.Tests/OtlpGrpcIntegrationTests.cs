using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using SData;
using SData.Ingest;
using SData.Ingest.OtlpGrpc;
using SData.Ingest.Services;
using SData.Store.Models;
using Xunit;

namespace SData.Ingest.Tests;

/// <summary>
/// OTLP gRPC 三服务集成测试（进程内 Kestrel h2c + GrpcChannel 客户端，T112）.
/// </summary>
public class OtlpGrpcIntegrationTests
{
    /// <summary>
    /// logs/traces/metrics 三通道推送经 gRPC 服务入缓冲.
    /// </summary>
    [Fact]
    public async Task Export_ThreeServices_EnqueueRecords()
    {
        var ingestor = new RecordingIngestor();
        using var app = StartGrpcApp(ingestor, out var port);

        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress($"http://127.0.0.1:{port}");

        var logsClient = new LogsService.LogsServiceClient(channel);
        var logsRequest = new ExportLogsServiceRequest();
        var logRecord = new OpenTelemetry.Proto.Logs.V1.LogRecord
        {
            TimeUnixNano = 1_741_998_600_000_000_000,
            SeverityText = "WARN",
            Body = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "grpc log body" },
        };
        logsRequest.ResourceLogs.Add(new ResourceLogs
        {
            Resource = new Resource { Attributes = { new OpenTelemetry.Proto.Common.V1.KeyValue { Key = "service.name", Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "grpc-svc" } } } },
            ScopeLogs = { new ScopeLogs { LogRecords = { logRecord } } },
        });
        var logsResponse = await logsClient.ExportAsync(logsRequest);
        Assert.Equal(0, logsResponse.PartialSuccess?.RejectedLogRecords ?? 0);
        Assert.Single(ingestor.Logs);
        Assert.Equal("grpc-svc", ingestor.Logs[0].Service);
        Assert.Equal("WARN", ingestor.Logs[0].Severity);

        var tracesClient = new TraceService.TraceServiceClient(channel);
        var traceRequest = new ExportTraceServiceRequest();
        traceRequest.ResourceSpans.Add(new ResourceSpans
        {
            Resource = new Resource { Attributes = { new OpenTelemetry.Proto.Common.V1.KeyValue { Key = "service.name", Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "grpc-svc" } } } },
            ScopeSpans = { new ScopeSpans { Spans = { new Span { TraceId = Google.Protobuf.ByteString.CopyFromUtf8("t-grpc"), SpanId = Google.Protobuf.ByteString.CopyFromUtf8("s-grpc"), Name = "op", Kind = Span.Types.SpanKind.Server, StartTimeUnixNano = 1, EndTimeUnixNano = 2 } } } },
        });
        var traceResponse = await tracesClient.ExportAsync(traceRequest);
        Assert.Equal(0, traceResponse.PartialSuccess?.RejectedSpans ?? 0);
        Assert.Single(ingestor.Spans);
        Assert.Equal("grpc-svc", ingestor.Spans[0].Service);

        var metricsClient = new MetricsService.MetricsServiceClient(channel);
        var metricsRequest = new ExportMetricsServiceRequest();
        metricsRequest.ResourceMetrics.Add(new ResourceMetrics
        {
            Resource = new Resource { Attributes = { new OpenTelemetry.Proto.Common.V1.KeyValue { Key = "service.name", Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "grpc-svc" } } } },
            ScopeMetrics = { new ScopeMetrics
            {
                Metrics = { new Metric { Name = "cpu", Gauge = new Gauge { DataPoints = { new NumberDataPoint { AsDouble = 12.5, TimeUnixNano = 1_741_998_600_000_000_000 } } } } },
            } },
        });
        var metricsResponse = await metricsClient.ExportAsync(metricsRequest);
        Assert.Equal(0, metricsResponse.PartialSuccess?.RejectedDataPoints ?? 0);
        Assert.Single(ingestor.Metrics);
        Assert.Equal("cpu", ingestor.Metrics[0].Name);
        Assert.Equal(12.5, ingestor.Metrics[0].Value);

        await app.StopAsync();
    }

    /// <summary>
    /// 缓冲满时 partialSuccess 上报拒绝数.
    /// </summary>
    [Fact]
    public async Task Export_BufferFull_ReportsPartialSuccess()
    {
        var ingestor = new RecordingIngestor { Full = true };
        using var app = StartGrpcApp(ingestor, out var port);

        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
        var logsClient = new LogsService.LogsServiceClient(channel);
        var request = new ExportLogsServiceRequest();
        request.ResourceLogs.Add(new ResourceLogs
        {
            Resource = new Resource { Attributes = { new OpenTelemetry.Proto.Common.V1.KeyValue { Key = "service.name", Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "grpc-svc" } } } },
            ScopeLogs = { new ScopeLogs { LogRecords = { new OpenTelemetry.Proto.Logs.V1.LogRecord { SeverityText = "INFO", Body = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "x" } } } } },
        });
        var response = await logsClient.ExportAsync(request);

        Assert.Equal(1, response.PartialSuccess.RejectedLogRecords);
        Assert.Empty(ingestor.Logs);
        await app.StopAsync();
    }

    /// <summary>
    /// 内置标准端口生效：ConfigureGrpcModule.ApplyListeners 固化 OTLP gRPC 端口（默认 4317，测试注入空闲端口避让冲突），
    /// gRPC 客户端经该端口调用成功（grpc=纯 HTTP/2 生效）.
    /// </summary>
    [Fact]
    public async Task BuiltinGrpcPort_ServesGrpc()
    {
        var ingestor = new RecordingIngestor();
        var port = GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<IIngestIngestor>(ingestor);
        builder.Services.AddSingleton<IOtlpProtoParser, OtlpProtoParserService>();

        // 模拟 ConfigureGrpcModule 的 Kestrel 配置路径：主端口 + 内置 OTLP gRPC 端口
        builder.Services.Configure<KestrelServerOptions>(options => ConfigureGrpcModule.ApplyListeners(options, GetFreePort(), otlpGrpcPort: port));

        var app = builder.Build();
        app.MapGrpcService<OtlpGrpcLogsService>();
        await app.StartAsync();

        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
        var client = new LogsService.LogsServiceClient(channel);
        var request = new ExportLogsServiceRequest();
        request.ResourceLogs.Add(new ResourceLogs
        {
            Resource = new Resource { Attributes = { new OpenTelemetry.Proto.Common.V1.KeyValue { Key = "service.name", Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "listener-svc" } } } },
            ScopeLogs = { new ScopeLogs { LogRecords = { new OpenTelemetry.Proto.Logs.V1.LogRecord { SeverityText = "INFO", Body = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "via-listener" } } } } },
        });
        await client.ExportAsync(request);

        Assert.Single(ingestor.Logs);
        Assert.Equal("listener-svc", ingestor.Logs[0].Service);
        await app.StopAsync();
    }

    /// <summary>
    /// 启动仅挂 OTLP gRPC 服务的最小应用（随机空闲端口；Kestrel 显式启用 HTTP/2 明文 h2c）.
    /// </summary>
    private static WebApplication StartGrpcApp(IIngestIngestor ingestor, out int port)
    {
        port = GetFreePort();
        var listenPort = port;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // 明文端点（h2c）需显式 Http2：Kestrel 明文 Http1AndHttp2 实际只服务 HTTP/1
        builder.Services.Configure<KestrelServerOptions>(options => options.ListenAnyIP(listenPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<IIngestIngestor>(ingestor);
        builder.Services.AddSingleton<IOtlpProtoParser, OtlpProtoParserService>();
        var app = builder.Build();
        app.MapGrpcService<OtlpGrpcLogsService>();
        app.MapGrpcService<OtlpGrpcTracesService>();
        app.MapGrpcService<OtlpGrpcMetricsService>();
        app.Start();
        return app;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 测试用记录摄取器（容量可控，模拟缓冲满）.
    /// </summary>
    private sealed class RecordingIngestor : IIngestIngestor
    {
        private readonly object _lock = new();

        public bool Full { get; init; }

        public List<SData.Store.Models.LogRecord> Logs { get; } = new();

        public List<SpanRecord> Spans { get; } = new();

        public List<MetricPointRecord> Metrics { get; } = new();

        public bool EnqueueLog(SData.Store.Models.LogRecord record)
        {
            if (Full)
            {
                return false;
            }

            lock (_lock)
            {
                Logs.Add(record);
            }

            return true;
        }

        public bool EnqueueSpan(SpanRecord record)
        {
            if (Full)
            {
                return false;
            }

            lock (_lock)
            {
                Spans.Add(record);
            }

            return true;
        }

        public bool EnqueueMetric(MetricPointRecord record)
        {
            if (Full)
            {
                return false;
            }

            lock (_lock)
            {
                Metrics.Add(record);
            }

            return true;
        }

        public bool EnqueueTelemetry(TelemetryRecord record) => !Full;

        public (long Accepted, long Dropped) EnqueueLogs(IReadOnlyList<SData.Store.Models.LogRecord> records)
        {
            if (Full)
            {
                return (0, records.Count);
            }

            lock (_lock)
            {
                Logs.AddRange(records);
            }

            return (records.Count, 0);
        }

        public (long Accepted, long Dropped) EnqueueSpans(IReadOnlyList<SpanRecord> records)
        {
            if (Full)
            {
                return (0, records.Count);
            }

            lock (_lock)
            {
                Spans.AddRange(records);
            }

            return (records.Count, 0);
        }

        public (long Accepted, long Dropped) EnqueueMetrics(IReadOnlyList<MetricPointRecord> records)
        {
            if (Full)
            {
                return (0, records.Count);
            }

            lock (_lock)
            {
                Metrics.AddRange(records);
            }

            return (records.Count, 0);
        }

        public IngestResponse IngestLogs(IEnumerable<IngestLogItem> items) => new();

        public IngestResponse IngestSpans(IEnumerable<IngestSpanItem> items) => new();

        public IngestResponse IngestMetrics(IEnumerable<IngestMetricItem> items) => new();

        public IngestResponse IngestTelemetry(IEnumerable<IngestTelemetryItem> items) => new();
    }
}
