using System.Text;
using System.Text.Json;
using global::Xunit;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using SData.Infra.Configuration;
using SData.Ingest.Consumers;
using SData.Ingest.Services;
using SData.Store;
using SData.Store.Models;

namespace SData.Ingest.Tests;

/// <summary>
/// MQ 消息日志解码测试：8 种解析规则（otlp-proto/otlp-json/json/regex/csv/logfmt/syslog/raw）→ LogRecord；
/// 解析失败产生带 _parse_error 的预览记录；真实消费遇失败/空载荷时阻塞重试，不确认.
/// </summary>
public class MqLogDecoderTests
{
    private readonly MqLogDecoder _decoder = new(new OtlpProtoParserService());

    /// <summary>
    /// otlp-proto：ExportLogsServiceRequest 字节 → 记录，并打上来源标记.
    /// </summary>
    [Fact]
    public void TryDecode_OtlpProtoBytes_ExtractsFieldsAndTagsSource()
    {
        var payload = BuildOtlpProtoBytes();

        var ok = _decoder.TryDecode(payload, Topic(MqLogFormat.OtlpProto), "kafka:logs-otlp", out var records, out var error);

        Assert.True(ok);
        Assert.Null(error);
        var record = Assert.Single(records);
        Assert.Equal("order-service", record.Service);
        Assert.Equal("WARN", record.Severity);
        Assert.Equal("slow query", record.Body);
        Assert.Equal("kafka:logs-otlp", record.Source);
    }

    /// <summary>
    /// otlp-json：OTLP JSON → 记录.
    /// </summary>
    [Fact]
    public void TryDecode_OtlpJson_ParsesResourceLogs()
    {
        const string payload = """
            {"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"svc-a"}}]},"scopeLogs":[{"logRecords":[{"timeUnixNano":"1747000000000000000","severityText":"ERROR","body":{"stringValue":"mq json log"}}]}]}]}
            """;

        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes(payload), Topic(MqLogFormat.OtlpJson), "rabbitmq:q", out var records, out var error);

        Assert.True(ok);
        Assert.Null(error);
        var record = Assert.Single(records);
        Assert.Equal("svc-a", record.Service);
        Assert.Equal("ERROR", record.Severity);
        Assert.Equal("mq json log", record.Body);
        Assert.Equal("rabbitmq:q", record.Source);
    }

    /// <summary>
    /// json：单对象（/ingest/logs 形状）→ 记录；topic 配置的默认服务名覆盖空 service.
    /// </summary>
    [Fact]
    public void TryDecode_JsonObject_AppliesDefaultsAndNormalizesSeverity()
    {
        const string payload = """{"body":"hello","severity":"warn","timestampMs":1747000000000}""";

        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes(payload), Topic(MqLogFormat.Json, service: "myapp"), "pulsar:t", out var records, out _);

        Assert.True(ok);
        var record = Assert.Single(records);
        Assert.Equal("myapp", record.Service);
        Assert.Equal("WARN", record.Severity);
        Assert.Equal("hello", record.Body);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1747000000000), record.Timestamp);
    }

    /// <summary>
    /// json：对象数组 → 多条记录；数组内非对象元素按失败策略原文入库（_parse_error）.
    /// </summary>
    [Fact]
    public void TryDecode_JsonArray_NonObjectElementsBecomeFallback()
    {
        const string payload = """[{"service":"a","body":"first"},"text-item",42]""";

        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes(payload), Topic(MqLogFormat.Json), string.Empty, out var records, out var error);

        Assert.True(ok);
        Assert.NotNull(error);
        Assert.Equal(3, records.Count);
        Assert.Equal("a", records[0].Service);
        Assert.Equal("first", records[0].Body);
        Assert.Null(GetParseError(records[0]));
        Assert.Equal("\"text-item\"", records[1].Body);
        Assert.NotNull(GetParseError(records[1]));
        Assert.Equal("42", records[2].Body);
        Assert.NotNull(GetParseError(records[2]));
    }

    /// <summary>
    /// json：字段映射模式（时间/级别/服务/正文字段路径）→ 通用提取，未映射字段进 attributes.
    /// </summary>
    [Fact]
    public void TryDecode_JsonWithFieldMapping_ExtractsConfiguredFields()
    {
        const string payload = """{"ts":"2026-05-12T10:00:00Z","lvl":"warning","svc":"api","message":"disk almost full","extra":{"node":"n1"}}""";
        var parser = new MqParserOptions
        {
            TimestampField = "ts",
            SeverityField = "lvl",
            ServiceField = "svc",
            BodyField = "message",
        };

        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes(payload), Topic(MqLogFormat.Json, parser: parser), string.Empty, out var records, out var error);

        Assert.True(ok);
        Assert.Null(error);
        var record = Assert.Single(records);
        Assert.Equal("api", record.Service);
        Assert.Equal("WARN", record.Severity);
        Assert.Equal("disk almost full", record.Body);
        Assert.Equal(new DateTimeOffset(2026, 5, 12, 10, 0, 0, TimeSpan.Zero), record.Timestamp);
        Assert.NotNull(record.AttributesJson);
        Assert.Contains("extra.node", record.AttributesJson);
        Assert.Contains("n1", record.AttributesJson);
    }

    /// <summary>
    /// regex：log4j/logback 形态命名捕获组映射 → 字段提取，线程/logger 进 attributes.
    /// </summary>
    [Fact]
    public void TryDecode_RegexLog4jLine_ExtractsNamedGroups()
    {
        const string pattern = @"(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\s+\[(?<thread>[^\]]+)\]\s+(?<level>\w+)\s+(?<logger>\S+)\s+-\s+(?<msg>.*)";
        var parser = new MqParserOptions
        {
            Pattern = pattern,
            TimestampField = "ts",
            SeverityField = "level",
            BodyField = "msg",
        };
        const string line = "2026-11-16 10:30:45.123 [main] ERROR com.foo.Bar - boom happened";

        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes(line), Topic(MqLogFormat.Regex, service: "order-api", parser: parser), "kafka:app", out var records, out var error);

        Assert.True(ok);
        Assert.Null(error);
        var record = Assert.Single(records);
        Assert.Equal("order-api", record.Service);
        Assert.Equal("ERROR", record.Severity);
        Assert.Equal("boom happened", record.Body);
        Assert.Equal(new DateTime(2026, 11, 16, 10, 30, 45, 123), record.Timestamp.LocalDateTime);
        Assert.Equal("kafka:app", record.Source);
        Assert.NotNull(record.AttributesJson);
        Assert.Contains("main", record.AttributesJson);
    }

    /// <summary>
    /// regex：多行消息中未匹配行（如异常堆栈续行）并入上一条正文，不产生独立失败行.
    /// </summary>
    [Fact]
    public void TryDecode_RegexMultilineStack_AppendsContinuationToPreviousBody()
    {
        var parser = new MqParserOptions
        {
            Pattern = @"(?<ts>\S+ \S+)\s+(?<level>\w+)\s+(?<msg>.*)",
            TimestampField = "ts",
            SeverityField = "level",
            BodyField = "msg",
        };
        const string payload = "2026-11-16 10:30:45 ERROR boom happened\n    at com.foo.Bar.run(Bar.java:42)\n    at com.foo.Baz.call(Baz.java:7)";

        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes(payload), Topic(MqLogFormat.Regex, parser: parser), string.Empty, out var records, out var error);

        Assert.True(ok);
        Assert.Null(error);
        var record = Assert.Single(records);
        Assert.Equal("boom happened\n    at com.foo.Bar.run(Bar.java:42)\n    at com.foo.Baz.call(Baz.java:7)", record.Body);
    }

    /// <summary>
    /// regex：首行即不匹配 → 失败策略原文入库（_parse_error 标记）.
    /// </summary>
    [Fact]
    public void TryDecode_RegexNoMatch_FallbackStoresRawLine()
    {
        var parser = new MqParserOptions { Pattern = @"(?<level>ERROR|WARN) (?<msg>.*)" };

        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes("completely unrelated text"), Topic(MqLogFormat.Regex, severity: "INFO", parser: parser), "kafka:x", out var records, out var error);

        Assert.True(ok);
        Assert.NotNull(error);
        var record = Assert.Single(records);
        Assert.Equal("completely unrelated text", record.Body);
        Assert.Equal("INFO", record.Severity);
        Assert.True(IsParseErrorMarked(record));
    }

    /// <summary>
    /// csv：分隔符分列 + 列索引映射（时间/级别/服务/正文）.
    /// </summary>
    [Fact]
    public void TryDecode_CsvLine_MapsColumnIndexes()
    {
        var parser = new MqParserOptions
        {
            Delimiter = ",",
            TimestampField = "0",
            TimestampFormat = "yyyy-MM-dd HH:mm:ss",
            SeverityField = "1",
            ServiceField = "2",
            BodyField = "3",
        };
        const string payload = "2026-11-16 10:30:45,ERROR,order-api,payment failed";

        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes(payload), Topic(MqLogFormat.Csv, parser: parser), "kafka:csv", out var records, out var error);

        Assert.True(ok);
        Assert.Null(error);
        var record = Assert.Single(records);
        Assert.Equal("order-api", record.Service);
        Assert.Equal("ERROR", record.Severity);
        Assert.Equal("payment failed", record.Body);
        Assert.Equal(new DateTime(2026, 11, 16, 10, 30, 45), record.Timestamp.LocalDateTime);
        Assert.Null(record.AttributesJson);
    }

    /// <summary>
    /// logfmt：key=value 解析 + 别名归一（w→WARN）+ trace 字段.
    /// </summary>
    [Fact]
    public void TryDecode_LogfmtLine_ParsesKeyValuePairs()
    {
        const string payload = """level=w svc=payments msg="payment timeout" trace_id=abc123""";
        var parser = new MqParserOptions
        {
            SeverityField = "level",
            ServiceField = "svc",
            BodyField = "msg",
            TraceIdField = "trace_id",
        };

        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes(payload), Topic(MqLogFormat.Logfmt, parser: parser), "pulsar:kv", out var records, out var error);

        Assert.True(ok);
        Assert.Null(error);
        var record = Assert.Single(records);
        Assert.Equal("payments", record.Service);
        Assert.Equal("WARN", record.Severity);
        Assert.Equal("payment timeout", record.Body);
        Assert.Equal("abc123", record.TraceId);
        Assert.Null(record.AttributesJson);
    }

    /// <summary>
    /// syslog：RFC5424 解析 → PRI 取模映射级别、app 映射服务、msg 映射正文.
    /// </summary>
    [Fact]
    public void TryDecode_SyslogRfc5424_MapsPriAppAndMsg()
    {
        const string payload = """<34>1 2026-11-16T08:30:00.000Z web-01 app-name 1234 ID47 [meta x="1"] disk usage high""";

        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes(payload), Topic(MqLogFormat.Syslog), "kafka:sys", out var records, out var error);

        Assert.True(ok);
        Assert.Null(error);
        var record = Assert.Single(records);
        Assert.Equal("app-name", record.Service);
        Assert.Equal("ERROR", record.Severity);
        Assert.Equal("disk usage high", record.Body);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 8, 30, 0, TimeSpan.Zero), record.Timestamp);
        Assert.NotNull(record.AttributesJson);
        Assert.Contains("web-01", record.AttributesJson);
    }

    /// <summary>
    /// syslog：RFC3164 解析（无年份，按当前年份嗅探）→ tag 映射服务.
    /// </summary>
    [Fact]
    public void TryDecode_SyslogRfc3164_MapsTagAndMsg()
    {
        const string payload = """<13>Nov 16 08:30:00 web-01 sshd[4321]: accepted publickey for root""";

        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes(payload), Topic(MqLogFormat.Syslog), string.Empty, out var records, out var error);

        Assert.True(ok);
        Assert.Null(error);
        var record = Assert.Single(records);
        Assert.Equal("sshd", record.Service);
        Assert.Equal("INFO", record.Severity);
        Assert.Equal("accepted publickey for root", record.Body);
    }

    /// <summary>
    /// raw：纯文本行 → 正文直取，service/severity 用配置默认值，来源打标.
    /// </summary>
    [Fact]
    public void TryDecode_RawLine_UsesConfiguredDefaults()
    {
        var payload = Encoding.UTF8.GetBytes("connection refused after 3 retries");

        var ok = _decoder.TryDecode(payload, Topic(MqLogFormat.Raw, service: "gateway", severity: "error"), "kafka:logs-text", out var records, out _);

        Assert.True(ok);
        var record = Assert.Single(records);
        Assert.Equal("gateway", record.Service);
        Assert.Equal("ERROR", record.Severity);
        Assert.Equal("connection refused after 3 retries", record.Body);
        Assert.Equal("kafka:logs-text", record.Source);
    }

    /// <summary>
    /// raw：行首 RFC3339 时间戳被识别提取，余下部分为正文.
    /// </summary>
    [Fact]
    public void TryDecode_RawLineWithRfc3339Prefix_ExtractsTimestamp()
    {
        var payload = Encoding.UTF8.GetBytes("2026-11-16T08:30:00Z upstream timeout");

        var ok = _decoder.TryDecode(payload, Topic(MqLogFormat.Raw, service: "gateway"), string.Empty, out var records, out _);

        Assert.True(ok);
        var record = Assert.Single(records);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 8, 30, 0, TimeSpan.Zero), record.Timestamp);
        Assert.Equal("upstream timeout", record.Body);
    }

    /// <summary>
    /// 载荷与规则严重不符（proto 垃圾字节 / 畸形 JSON）：原文入库不丢数据（_parse_error 标记）.
    /// </summary>
    [Theory]
    [InlineData(MqLogFormat.OtlpProto, "not-proto-bytes")]
    [InlineData(MqLogFormat.OtlpJson, "{broken json")]
    [InlineData(MqLogFormat.Json, "{broken json")]
    public void TryDecode_CorruptPayload_FallbackStoresRaw(string format, string payloadText)
    {
        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes(payloadText), Topic(format), string.Empty, out var records, out var error);

        Assert.True(ok);
        Assert.NotNull(error);
        var record = Assert.Single(records);
        Assert.Equal(payloadText, record.Body);
        Assert.True(IsParseErrorMarked(record));
    }

    /// <summary>
    /// 未知格式 / 空载荷返回 false，真实消费必须保持未确认并阻塞重试.
    /// </summary>
    [Theory]
    [InlineData("xml", "<log>x</log>")]
    [InlineData("xml", "")]
    [InlineData(MqLogFormat.Raw, "")]
    public void TryDecode_UnknownFormatOrEmptyPayload_ReturnsFalse(string format, string payloadText)
    {
        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes(payloadText), Topic(format), string.Empty, out var records, out _);

        Assert.False(ok);
        Assert.Empty(records);
    }

    /// <summary>
    /// 级别别名归一：warning→WARN、crit→ERROR；自定义映射优先于内置表.
    /// </summary>
    [Fact]
    public void NormalizeSeverity_AliasesAndCustomMap()
    {
        Assert.Equal("WARN", TextLogMapping.NormalizeSeverity("warning", "INFO"));
        Assert.Equal("ERROR", TextLogMapping.NormalizeSeverity("crit", "INFO"));
        Assert.Equal("FATAL", TextLogMapping.NormalizeSeverity("emerg", "INFO"));
        Assert.Equal("INFO", TextLogMapping.NormalizeSeverity(null, "INFO"));
        Assert.Equal("ERROR", TextLogMapping.NormalizeSeverity("E", "INFO", new Dictionary<string, string> { ["E"] = "error" }));
    }

    /// <summary>
    /// 时间解析：epoch 毫秒/秒、自定义格式串、RFC3339、失败回退.
    /// </summary>
    [Fact]
    public void ParseTimestamp_EpochAndFormatsAndFallback()
    {
        var fallback = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1747000000000), TextLogMapping.ParseTimestamp("1747000000000", null, fallback));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1747000000), TextLogMapping.ParseTimestamp("1747000000", null, fallback));
        Assert.Equal(new DateTime(2026, 11, 16, 10, 30, 45), TextLogMapping.ParseTimestamp("2026-11-16 10:30:45", "yyyy-MM-dd HH:mm:ss", fallback).LocalDateTime);
        Assert.Equal(fallback, TextLogMapping.ParseTimestamp("not-a-time", "yyyy-MM-dd HH:mm:ss", fallback));
        Assert.Equal(fallback, TextLogMapping.ParseTimestamp("", null, fallback));
        Assert.Equal(new DateTimeOffset(2026, 5, 12, 10, 0, 0, TimeSpan.Zero), TextLogMapping.ParseTimestamp("2026-05-12T10:00:00Z", null, fallback));
    }

    /// <summary>
    /// 格式支持校验（8 种白名单）.
    /// </summary>
    [Fact]
    public void IsSupported_KnownFormats_TrueOtherwiseFalse()
    {
        Assert.True(MqLogFormat.IsSupported(MqLogFormat.OtlpProto));
        Assert.True(MqLogFormat.IsSupported(MqLogFormat.OtlpJson));
        Assert.True(MqLogFormat.IsSupported(MqLogFormat.Json));
        Assert.True(MqLogFormat.IsSupported(MqLogFormat.Regex));
        Assert.True(MqLogFormat.IsSupported(MqLogFormat.Csv));
        Assert.True(MqLogFormat.IsSupported(MqLogFormat.Logfmt));
        Assert.True(MqLogFormat.IsSupported(MqLogFormat.Syslog));
        Assert.True(MqLogFormat.IsSupported(MqLogFormat.Raw));
        Assert.False(MqLogFormat.IsSupported("xml"));
        Assert.False(MqLogFormat.IsSupported(null));
    }

    /// <summary>
    /// 缓冲满时退避重试直到全部入队（at-least-once，不丢数据）.
    /// </summary>
    [Fact]
    public async Task EnqueueAllAsync_BufferFull_RetriesUntilAccepted()
    {
        var buffer = new FlakyBuffer(failFirst: 2);
        var records = new List<SData.Store.Models.LogRecord>
        {
            new() { Service = "svc", Body = "m1" },
            new() { Service = "svc", Body = "m2" },
        };

        await MqConsumerSupport.EnqueueAllAsync(buffer, records, delay: 1, cancellationToken: CancellationToken.None);

        Assert.Equal(4, buffer.EnqueueCalls);
        Assert.Equal(2, buffer.Logs.Count);
        Assert.Equal("m2", buffer.Logs[1].Body);
    }

    /// <summary>
    /// 解析预览器：规则命中 → Success + 记录；规则未命中 → Success=false + _parse_error 记录.
    /// </summary>
    [Fact]
    public void Previewer_RegexSample_ReturnsRecordsOrError()
    {
        var previewer = new MqLogPreviewer(_decoder);
        var parser = new MqParserOptions { Pattern = @"(?<level>ERROR|WARN) (?<msg>.*)", SeverityField = "level", BodyField = "msg" };
        var hit = previewer.Preview("ERROR boom", Topic(MqLogFormat.Regex, service: "api", parser: parser));
        Assert.True(hit.Success);
        Assert.Null(hit.Error);
        Assert.Equal("ERROR", hit.Records[0].Severity);
        Assert.Equal("boom", hit.Records[0].Body);

        var miss = previewer.Preview("unrelated", Topic(MqLogFormat.Regex, service: "api", parser: parser));
        Assert.False(miss.Success);
        Assert.NotNull(miss.Error);
        Assert.True(miss.Records[0].Body == "unrelated");
        Assert.Contains("_parse_error", miss.Records[0].AttributesJson);
    }

    /// <summary>
    /// 构造 OTLP logs proto 字节（资源服务名 + WARN 日志）.
    /// </summary>
    private static byte[] BuildOtlpProtoBytes()
    {
        var request = new ExportLogsServiceRequest();
        var resourceLogs = new ResourceLogs();
        resourceLogs.Resource = new Resource();
        resourceLogs.Resource.Attributes.Add(new KeyValue
        {
            Key = "service.name",
            Value = new AnyValue { StringValue = "order-service" },
        });
        var scopeLogs = new ScopeLogs();
        scopeLogs.LogRecords.Add(new OpenTelemetry.Proto.Logs.V1.LogRecord
        {
            TimeUnixNano = 1747000000000000000UL,
            SeverityText = "WARN",
            Body = new AnyValue { StringValue = "slow query" },
        });
        resourceLogs.ScopeLogs.Add(scopeLogs);
        request.ResourceLogs.Add(resourceLogs);
        return request.ToByteArray();
    }

    /// <summary>
    /// 记录是否带 _parse_error 标记.
    /// </summary>
    private static bool IsParseErrorMarked(SData.Store.Models.LogRecord record)
    {
        if (string.IsNullOrEmpty(record.AttributesJson))
        {
            return false;
        }

        using var document = JsonDocument.Parse(record.AttributesJson);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("_parse_error", out _);
    }

    /// <summary>
    /// 从记录 attributes 提取 _parse_error（测试辅助）.
    /// </summary>
    private static string? GetParseError(SData.Store.Models.LogRecord record)
    {
        if (string.IsNullOrEmpty(record.AttributesJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(record.AttributesJson);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("_parse_error", out var element)
            ? element.GetString()
            : null;
    }

    /// <summary>
    /// 构造 topic 解析规则.
    /// </summary>
    private static MqTopicOptions Topic(string format, string service = "", string severity = "INFO", MqParserOptions? parser = null)
    {
        return new MqTopicOptions
        {
            Name = "t",
            Format = format,
            Service = service,
            Severity = severity,
            Parser = parser,
        };
    }

    /// <summary>
    /// 前 N 次 TryEnqueueLog 失败（模拟缓冲满），之后成功的假缓冲.
    /// </summary>
    private sealed class FlakyBuffer : IIngestBuffer
    {
        private readonly int _failFirst;

        public FlakyBuffer(int failFirst)
        {
            _failFirst = failFirst;
        }

        public int EnqueueCalls { get; private set; }

        public List<SData.Store.Models.LogRecord> Logs { get; } = new();

        public bool TryEnqueueLog(SData.Store.Models.LogRecord record)
        {
            EnqueueCalls++;
            if (EnqueueCalls <= _failFirst)
            {
                return false;
            }

            Logs.Add(record);
            return true;
        }

        public Task PersistLogsAsync(IReadOnlyList<SData.Store.Models.LogRecord> records, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public bool TryEnqueueSpan(SpanRecord record) => false;

        public bool TryEnqueueMetric(MetricPointRecord record) => false;

        public bool TryEnqueueTelemetry(TelemetryRecord record) => false;

        public bool TryEnqueueRumEvent(RumEventRecord record) => false;

        public bool TryEnqueueRumSession(RumSessionRecord record) => false;

        public bool TryEnqueueRumReplay(RumReplayRecord record) => false;

        public IngestBufferStats GetStats() => new();
    }
}
