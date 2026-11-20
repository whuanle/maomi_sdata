using global::Xunit;
using SData.Ingest.Services;
using SData.Store;
using SData.Store.Models;

namespace SData.Ingest.Tests;

/// <summary>
/// OTLP/HTTP(JSON) 解析测试.
/// </summary>
public class OtlpParserTests
{
    private readonly OtlpParser _parser = new();

    [Fact]
    public void ParseTraces_PreservesMicrosecondsAndOkStatus()
    {
        const string json = """
            {"resourceSpans":[{"scopeSpans":[{"spans":[{
              "traceId":"abc","spanId":"def","name":"cache",
              "startTimeUnixNano":"1747000000000123000",
              "endTimeUnixNano":"1747000000000579000",
              "status":{"code":1},
              "events":[{"name":"hit","timeUnixNano":"1747000000000223000",
                "attributes":[{"key":"cache.hit","value":{"boolValue":true}}]}]
            }]}]}]}
            """;
        var record = Assert.Single(_parser.ParseTraces(json));
        Assert.Equal(456, record.DurationUs);
        Assert.Equal(1230, (record.Timestamp - DateTimeOffset.FromUnixTimeMilliseconds(1747000000000)).Ticks);
        Assert.Equal("ok", record.Status);
        Assert.Contains("cache.hit", record.EventsJson);
    }

    /// <summary>
    /// OTLP logs 解析：resource 属性提取服务名、severity、body、trace 关联.
    /// </summary>
    [Fact]
    public void ParseLogs_ExtractsServiceAndSeverity()
    {
        const string json = """
            {
              "resourceLogs": [{
                "resource": {"attributes": [{"key": "service.name", "value": {"stringValue": "order-service"}}]},
                "scopeLogs": [{
                  "logRecords": [{
                    "timeUnixNano": "1747000000000000000",
                    "severityText": "WARN",
                    "body": {"stringValue": "slow query"},
                    "traceId": "aabb",
                    "spanId": "ccdd",
                    "attributes": [{"key": "db.statement", "value": {"stringValue": "SELECT 1"}}]
                  }]
                }]
              }]
            }
            """;

        var records = _parser.ParseLogs(json);
        var record = Assert.Single(records);
        Assert.Equal("order-service", record.Service);
        Assert.Equal("WARN", record.Severity);
        Assert.Equal("slow query", record.Body);
        Assert.Equal("aabb", record.TraceId);
        Assert.Equal("ccdd", record.SpanId);
        Assert.Contains("db.statement", record.AttributesJson);
    }

    /// <summary>
    /// OTLP logs：severityNumber 缺省文本时映射级别.
    /// </summary>
    [Fact]
    public void ParseLogs_MapsSeverityNumber()
    {
        const string json = """
            {
              "resourceLogs": [{
                "scopeLogs": [{
                  "logRecords": [{"timeUnixNano": "1747000000000000000", "severityNumber": 17, "body": {"stringValue": "err"}}]
                }]
              }]
            }
            """;

        var records = _parser.ParseLogs(json);
        Assert.Equal("ERROR", records[0].Severity);
    }

    /// <summary>
    /// OTLP traces 解析：span 关联与状态.
    /// </summary>
    [Fact]
    public void ParseTraces_ExtractsSpans()
    {
        const string json = """
            {
              "resourceSpans": [{
                "resource": {"attributes": [{"key": "service.name", "value": {"stringValue": "web"}}]},
                "scopeSpans": [{
                  "spans": [{
                    "traceId": "t1",
                    "spanId": "s1",
                    "name": "GET /api/orders",
                    "kind": 2,
                    "startTimeUnixNano": "1747000000000000000",
                    "endTimeUnixNano": "1747000000005000000",
                    "status": {"code": 2}
                  }]
                }]
              }]
            }
            """;

        var records = _parser.ParseTraces(json);
        var record = Assert.Single(records);
        Assert.Equal("web", record.Service);
        Assert.Equal("server", record.Kind);
        Assert.Equal("error", record.Status);
        Assert.Equal(5_000, record.DurationUs); // 5ms = 5000µs
    }

    /// <summary>
    /// OTLP metrics 解析：gauge asDouble 与 asInt、histogram 派生序列.
    /// </summary>
    [Fact]
    public void ParseMetrics_ExtractsPoints()
    {
        const string json = """
            {
              "resourceMetrics": [{
                "scopeMetrics": [{
                  "metrics": [
                    {"name": "cpu", "gauge": {"dataPoints": [{"asDouble": 12.5, "timeUnixNano": "1747000000000000000", "attributes": [{"key": "host", "value": {"stringValue": "a"}}]}]}},
                    {"name": "mem", "gauge": {"dataPoints": [{"asInt": "4096", "timeUnixNano": "1747000000000000000"}]}},
                    {"name": "latency", "histogram": {"dataPoints": [{"count": 10, "sum": 500.0, "timeUnixNano": "1747000000000000000"}]}}
                  ]
                }]
              }]
            }
            """;

        var records = _parser.ParseMetrics(json);
        Assert.Equal(4, records.Count);

        var cpu = records.First(x => x.Name == "cpu");
        Assert.Equal(12.5, cpu.Value);
        Assert.Contains("host", cpu.LabelsJson);

        Assert.Equal(4096, records.First(x => x.Name == "mem").Value);
        Assert.Equal(10, records.First(x => x.Name == "latency.count").Value);
        Assert.Equal(500.0, records.First(x => x.Name == "latency.sum").Value);
    }
}

/// <summary>
/// 接入缓冲测试.
/// </summary>
public class IngestBufferTests
{
    /// <summary>
    /// 缓冲写入后可读出，满负荷丢弃并计数.
    /// </summary>
    [Fact]
    public void Buffer_EnqueueDrain_CountsDropped()
    {
        var options = new SData.Infra.Configuration.SystemOptions();
        options.Ingest.BufferCapacity = 2;
        var buffer = new IngestBuffer(options);

        Assert.True(buffer.TryEnqueueLog(new LogRecord { Body = "1" }));
        Assert.True(buffer.TryEnqueueLog(new LogRecord { Body = "2" }));
        Assert.False(buffer.TryEnqueueLog(new LogRecord { Body = "3" }));

        var stats = buffer.GetStats();
        Assert.Equal(1, stats.Dropped["log"]);

        var drained = 0;
        while (buffer.Reader.TryRead(out var _))
        {
            drained++;
        }

        Assert.Equal(2, drained);
    }
}
