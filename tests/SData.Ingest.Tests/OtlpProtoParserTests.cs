using System.IO.Compression;
using System.Text;
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
using SData.Ingest.Services;

namespace SData.Ingest.Tests;

/// <summary>
/// OTLP/HTTP(proto) 解析测试（T111）：proto 字节与 gzip 链路解析为领域记录，与 JSON parser 语义对齐.
/// </summary>
public class OtlpProtoParserTests
{
    /// <summary>
    /// logs：proto + gzip 报文入库解析（资源服务名/severity/body/trace 关联/属性）.
    /// </summary>
    [Fact]
    public void ParseLogs_GzipProto_ExtractsFields()
    {
        var request = BuildLogsRequest();
        var bytes = Gzip(request.ToByteArray());

        var decoded = ExportLogsServiceRequest.Parser.ParseFrom(Gunzip(bytes));
        var records = OtlpProtoParser.ParseLogs(decoded);

        var record = Assert.Single(records);
        Assert.Equal("order-service", record.Service);
        Assert.Equal("WARN", record.Severity);
        Assert.Equal("slow query", record.Body);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", record.TraceId);
        Assert.Equal("b7ad6b7169203331", record.SpanId);
        Assert.Contains("db.statement", record.AttributesJson);
        Assert.Contains("service.name", record.ResourceJson);
    }

    /// <summary>
    /// logs：severityNumber 缺省文本时映射级别（17 → ERROR，与 JSON parser 分段一致）.
    /// </summary>
    [Fact]
    public void ParseLogs_MapsSeverityNumber()
    {
        var request = new ExportLogsServiceRequest();
        request.ResourceLogs.Add(new ResourceLogs { ScopeLogs = { new ScopeLogs() } });
        request.ResourceLogs[0].ScopeLogs[0].LogRecords.Add(new LogRecord
        {
            TimeUnixNano = 1747000000000000000UL,
            SeverityNumber = SeverityNumber.Error,
            Body = new AnyValue { StringValue = "boom" }
        });

        var records = OtlpProtoParser.ParseLogs(request);

        Assert.Equal("ERROR", Assert.Single(records).Severity);
    }

    /// <summary>
    /// traces：kind=Server、Status=Error 映射 server/error，时长毫秒→微秒.
    /// </summary>
    [Fact]
    public void ParseTraces_MapsKindStatusAndDuration()
    {
        var request = new ExportTraceServiceRequest();
        request.ResourceSpans.Add(new ResourceSpans
        {
            Resource = new Resource(),
            ScopeSpans = { new ScopeSpans() }
        });
        request.ResourceSpans[0].Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "gateway" } });
        request.ResourceSpans[0].ScopeSpans[0].Spans.Add(new Span
        {
            TraceId = ByteString.CopyFrom(new byte[16]),
            SpanId = ByteString.CopyFrom(new byte[8]),
            Name = "GET /api/orders",
            Kind = Span.Types.SpanKind.Server,
            StartTimeUnixNano = 1747000000000000000UL,
            EndTimeUnixNano = 1747000000005000000UL,
            Status = new Status { Code = Status.Types.StatusCode.Error }
        });

        var records = OtlpProtoParser.ParseTraces(request);

        var span = Assert.Single(records);
        Assert.Equal("gateway", span.Service);
        Assert.Equal("server", span.Kind);
        Assert.Equal("error", span.Status);
        Assert.Equal(5000, span.DurationUs);
        Assert.Null(span.ParentSpanId);
    }

    /// <summary>
    /// metrics：gauge/sum 直接入桶、histogram 生成 count/sum 派生序列、标签合并 service.
    /// </summary>
    [Fact]
    public void ParseMetrics_GaugeSumHistogram_ProducesExpectedSeries()
    {
        var request = new ExportMetricsServiceRequest();
        request.ResourceMetrics.Add(new ResourceMetrics
        {
            Resource = new Resource(),
            ScopeMetrics = { new ScopeMetrics() }
        });
        request.ResourceMetrics[0].Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "gateway" } });
        var scopeMetrics = request.ResourceMetrics[0].ScopeMetrics[0];

        var gauge = new Metric { Name = "cpu.usage", Gauge = new Gauge() };
        gauge.Gauge.DataPoints.Add(new NumberDataPoint { AsDouble = 33.5, TimeUnixNano = 1747000000000000000UL });
        gauge.Gauge.DataPoints[0].Attributes.Add(new KeyValue { Key = "cpu", Value = new AnyValue { StringValue = "0" } });
        scopeMetrics.Metrics.Add(gauge);

        var histogram = new Metric { Name = "latency", Histogram = new Histogram() };
        histogram.Histogram.DataPoints.Add(new HistogramDataPoint { Count = 120, Sum = 4500, TimeUnixNano = 1747000000000000000UL });
        scopeMetrics.Metrics.Add(histogram);

        var records = OtlpProtoParser.ParseMetrics(request);

        Assert.Equal(3, records.Count);
        var cpu = records.Single(r => r.Name == "cpu.usage");
        Assert.Equal("gauge", cpu.Kind);
        Assert.Equal(33.5, cpu.Value);
        Assert.Contains("\"service\":\"gateway\"", cpu.LabelsJson);
        Assert.Contains("cpu", cpu.LabelsJson);

        var count = records.Single(r => r.Name == "latency.count");
        Assert.Equal("histogram", count.Kind);
        Assert.Equal(120, count.Value);

        var sum = records.Single(r => r.Name == "latency.sum");
        Assert.Equal("histogram", sum.Kind);
        Assert.Equal(4500, sum.Value);
    }

    /// <summary>
    /// 坏字节 proto 报文解析抛 InvalidProtocolBufferException（支撑 Controller 400 语义）.
    /// </summary>
    [Fact]
    public void ParseLogs_InvalidProtoBytes_Throws()
    {
        var garbage = Encoding.UTF8.GetBytes("this is not protobuf");
        Assert.Throws<Google.Protobuf.InvalidProtocolBufferException>(
            () => ExportLogsServiceRequest.Parser.ParseFrom(garbage));
    }

    [Fact]
    public void ParseTraces_PreservesMicrosecondsAndEventAttributes()
    {
        var request = new ExportTraceServiceRequest();
        var span = new Span
        {
            StartTimeUnixNano = 1747000000000123000UL,
            EndTimeUnixNano = 1747000000000579000UL,
            Status = new Status { Code = Status.Types.StatusCode.Ok },
        };
        span.Events.Add(new Span.Types.Event
        {
            Name = "cache",
            TimeUnixNano = 1747000000000223000UL,
            Attributes = { new KeyValue { Key = "hit", Value = new AnyValue { BoolValue = true } } },
        });
        request.ResourceSpans.Add(new ResourceSpans { ScopeSpans = { new ScopeSpans { Spans = { span } } } });
        var record = Assert.Single(OtlpProtoParser.ParseTraces(request));
        Assert.Equal(456, record.DurationUs);
        Assert.Equal(1230, (record.Timestamp - DateTimeOffset.FromUnixTimeMilliseconds(1747000000000)).Ticks);
        Assert.Equal("ok", record.Status);
        using var document = System.Text.Json.JsonDocument.Parse(record.EventsJson!);
        Assert.True(document.RootElement[0].GetProperty("attributes").GetProperty("hit").GetBoolean());
        Assert.Equal("1747000000000223000", document.RootElement[0].GetProperty("timeUnixNano").GetString());
    }

    private static ExportLogsServiceRequest BuildLogsRequest()
    {
        var request = new ExportLogsServiceRequest();
        request.ResourceLogs.Add(new ResourceLogs
        {
            Resource = new Resource(),
            ScopeLogs = { new ScopeLogs() }
        });
        request.ResourceLogs[0].Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "order-service" } });
        var scopeLogs = request.ResourceLogs[0].ScopeLogs[0];
        scopeLogs.LogRecords.Add(new LogRecord
        {
            TimeUnixNano = 1747000000000000000UL,
            SeverityText = "WARN",
            Body = new AnyValue { StringValue = "slow query" },
            TraceId = ByteStringFromHex("0af7651916cd43dd8448eb211c80319c"),
            SpanId = ByteStringFromHex("b7ad6b7169203331")
        });
        scopeLogs.LogRecords[0].Attributes.Add(new KeyValue { Key = "db.statement", Value = new AnyValue { StringValue = "SELECT 1" } });
        return request;
    }

    private static ByteString ByteStringFromHex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return ByteString.CopyFrom(bytes);
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(bytes);
        }

        return output.ToArray();
    }

    private static byte[] Gunzip(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
