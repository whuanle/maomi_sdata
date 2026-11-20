using global::Xunit;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace SData.Ingest.Tests;

/// <summary>
/// OTLP proto 强类型冒烟测试（T110）：Grpc.Tools 生成的 opentelemetry-proto v1.3.2 类型
/// 可构造官方形状报文并完成序列化/反序列化往返，gRPC 服务基类可用.
/// </summary>
public class OtlpProtoSmokeTests
{
    /// <summary>
    /// ExportTraceServiceRequest 往返：resource/scope/span 全字段保真.
    /// </summary>
    [Fact]
    public void ExportTraceServiceRequest_RoundTrip_PreservesFields()
    {
        var request = new ExportTraceServiceRequest();
        request.ResourceSpans.Add(new ResourceSpans
        {
            Resource = new Resource(),
            ScopeSpans = { new ScopeSpans() }
        });
        request.ResourceSpans[0].Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = StringValue("gateway") });
        var scopeSpans = request.ResourceSpans[0].ScopeSpans[0];
        scopeSpans.Scope = new InstrumentationScope { Name = "test.scope", Version = "1.0.0" };
        scopeSpans.Spans.Add(new Span
        {
            TraceId = ByteStringFromHex("0af7651916cd43dd8448eb211c80319c"),
            SpanId = ByteStringFromHex("b7ad6b7169203331"),
            ParentSpanId = ByteStringFromHex("b7ad6b7169203330"),
            Name = "GET /api/orders",
            Kind = Span.Types.SpanKind.Server,
            StartTimeUnixNano = 1747000000000000000UL,
            EndTimeUnixNano = 1747000000500000000UL,
            Status = new Status { Code = Status.Types.StatusCode.Ok }
        });
        scopeSpans.Spans[0].Attributes.Add(new KeyValue { Key = "http.method", Value = StringValue("GET") });

        var decoded = ExportTraceServiceRequest.Parser.ParseFrom(request.ToByteArray());

        var resourceSpans = Assert.Single(decoded.ResourceSpans);
        Assert.Equal("gateway", Assert.Single(resourceSpans.Resource.Attributes).Value.StringValue);
        var decodedScopeSpans = Assert.Single(resourceSpans.ScopeSpans);
        Assert.Equal("test.scope", decodedScopeSpans.Scope.Name);
        var span = Assert.Single(decodedScopeSpans.Spans);
        Assert.Equal("GET /api/orders", span.Name);
        Assert.Equal(Span.Types.SpanKind.Server, span.Kind);
        Assert.Equal(1747000000000000000UL, span.StartTimeUnixNano);
        Assert.Equal(Status.Types.StatusCode.Ok, span.Status.Code);
        Assert.Equal(16, span.TraceId.Length);
        Assert.Equal(8, span.SpanId.Length);
    }

    /// <summary>
    /// ExportLogsServiceRequest 往返：severity/body/trace 关联保真.
    /// </summary>
    [Fact]
    public void ExportLogsServiceRequest_RoundTrip_PreservesFields()
    {
        var request = new ExportLogsServiceRequest();
        request.ResourceLogs.Add(new ResourceLogs { ScopeLogs = { new ScopeLogs() } });
        var scopeLogs = request.ResourceLogs[0].ScopeLogs[0];
        scopeLogs.LogRecords.Add(new LogRecord
        {
            TimeUnixNano = 1747000000000000000UL,
            SeverityNumber = SeverityNumber.Warn,
            SeverityText = "WARN",
            Body = StringValue("slow query"),
            TraceId = ByteStringFromHex("0af7651916cd43dd8448eb211c80319c"),
            SpanId = ByteStringFromHex("b7ad6b7169203331")
        });
        scopeLogs.LogRecords[0].Attributes.Add(new KeyValue { Key = "db.statement", Value = StringValue("SELECT 1") });

        var decoded = ExportLogsServiceRequest.Parser.ParseFrom(request.ToByteArray());

        var record = Assert.Single(Assert.Single(decoded.ResourceLogs).ScopeLogs).LogRecords.Single();
        Assert.Equal(SeverityNumber.Warn, record.SeverityNumber);
        Assert.Equal("WARN", record.SeverityText);
        Assert.Equal("slow query", record.Body.StringValue);
        Assert.Equal("SELECT 1", Assert.Single(record.Attributes).Value.StringValue);
    }

    /// <summary>
    /// ExportMetricsServiceRequest 往返：sum 单调计数点保真（含 temporality 与聚合属性）.
    /// </summary>
    [Fact]
    public void ExportMetricsServiceRequest_RoundTrip_PreservesFields()
    {
        var request = new ExportMetricsServiceRequest();
        request.ResourceMetrics.Add(new ResourceMetrics { ScopeMetrics = { new ScopeMetrics() } });
        var scopeMetrics = request.ResourceMetrics[0].ScopeMetrics[0];
        var sum = new Sum
        {
            AggregationTemporality = AggregationTemporality.Cumulative,
            IsMonotonic = true
        };
        sum.DataPoints.Add(new NumberDataPoint { AsDouble = 1024, TimeUnixNano = 1747000000000000000UL });
        sum.DataPoints[0].Attributes.Add(new KeyValue { Key = "route", Value = StringValue("/api/orders") });
        scopeMetrics.Metrics.Add(new Metric { Name = "http_server_requests_total", Sum = sum });

        var decoded = ExportMetricsServiceRequest.Parser.ParseFrom(request.ToByteArray());

        var metric = Assert.Single(Assert.Single(decoded.ResourceMetrics).ScopeMetrics).Metrics.Single();
        Assert.Equal("http_server_requests_total", metric.Name);
        Assert.True(metric.Sum.IsMonotonic);
        Assert.Equal(AggregationTemporality.Cumulative, metric.Sum.AggregationTemporality);
        var point = Assert.Single(metric.Sum.DataPoints);
        Assert.Equal(1024, point.AsDouble);
        Assert.Equal("/api/orders", Assert.Single(point.Attributes).Value.StringValue);
    }

    /// <summary>
    /// gRPC 服务基类与 PartialSuccess 响应类型可用（T112 实现服务的编译前提）.
    /// </summary>
    [Fact]
    public void GrpcServiceBaseClasses_AreAvailable()
    {
        Assert.True(typeof(TraceService.TraceServiceBase).IsAbstract);
        Assert.True(typeof(LogsService.LogsServiceBase).IsAbstract);
        Assert.True(typeof(MetricsService.MetricsServiceBase).IsAbstract);

        // 响应可构造、PartialSuccess 子消息可用（生成代码中子消息属性默认 null，须显式赋值）
        Assert.Equal(0, new ExportTraceServiceResponse { PartialSuccess = new ExportTracePartialSuccess() }.PartialSuccess.RejectedSpans);
        Assert.Equal(0, new ExportLogsServiceResponse { PartialSuccess = new ExportLogsPartialSuccess() }.PartialSuccess.RejectedLogRecords);
        Assert.Equal(0, new ExportMetricsServiceResponse { PartialSuccess = new ExportMetricsPartialSuccess() }.PartialSuccess.RejectedDataPoints);
    }

    private static AnyValue StringValue(string value) => new() { StringValue = value };

    private static Google.Protobuf.ByteString ByteStringFromHex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return Google.Protobuf.ByteString.CopyFrom(bytes);
    }
}
