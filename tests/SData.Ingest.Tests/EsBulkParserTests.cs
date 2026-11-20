using global::Xunit;
using System.Text.Json;
using SData.Ingest.Services;
using SData.Store.Models;

namespace SData.Ingest.Tests;

/// <summary>
/// ES Bulk NDJSON 解析与 429 背压语义测试.
/// </summary>
public class EsBulkParserTests
{
    /// <summary>
    /// index/create 混合批次：_index 映射服务名与属性、级别/时间戳/trace 关联.
    /// </summary>
    [Fact]
    public void Parse_IndexAndCreate_MapsLogRecords()
    {
        const string ndjson = """
            {"index":{"_index":"logs-2026.11.15","_id":"1"}}
            {"@timestamp":"2026-11-15T12:00:00Z","log.level":"warn","message":"disk usage high","trace.id":"t1","span.id":"s1"}
            {"create":{"_index":"logs-2026.11.15"}}
            {"@timestamp":1747000000000,"message":"connection reset"}
            """;

        var result = EsBulkParser.Parse(ndjson);

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, i => Assert.True(i.Success));
        Assert.False(result.HasFailure);

        var first = result.Items[0].Record!;
        Assert.Equal("logs-2026.11.15", first.Service);
        Assert.Equal("logs-2026.11.15", first.Source);
        Assert.Equal("WARN", first.Severity);
        Assert.Contains("disk usage high", first.Body);
        Assert.Equal("t1", first.TraceId);
        Assert.Equal("s1", first.SpanId);
        Assert.Contains("\"_index\"", first.AttributesJson);
        Assert.Contains("\"1\"", first.AttributesJson);
        Assert.Equal(DateTimeOffset.Parse("2026-11-15T12:00:00Z"), first.Timestamp);

        var second = result.Items[1].Record!;
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1747000000000), second.Timestamp);
        Assert.Equal("INFO", second.Severity);
    }

    /// <summary>
    /// update/delete 动作：返回 ES 形状错误项且配对不错位.
    /// </summary>
    [Fact]
    public void Parse_UpdateAndDelete_FailWithEsShape()
    {
        const string ndjson = """
            {"update":{"_index":"logs-1","_id":"2"}}
            {"doc":{"message":"patched"}}
            {"delete":{"_index":"logs-1","_id":"3"}}
            {"index":{"_index":"logs-1","_id":"4"}}
            {"message":"after mixed actions"}
            """;

        var result = EsBulkParser.Parse(ndjson);

        Assert.Equal(3, result.Items.Count);
        Assert.True(result.HasFailure);

        Assert.False(result.Items[0].Success);
        Assert.Equal("update", result.Items[0].Action);
        Assert.Equal("illegal_argument_exception", result.Items[0].ErrorType);
        Assert.True(result.Items[1].Success is false && result.Items[1].Action == "delete");

        var valid = result.Items[2];
        Assert.True(valid.Success);
        Assert.Equal("4", valid.DocumentId);
    }

    /// <summary>
    /// 坏文档行：单项失败（parse_exception），其余项不受影响（errors:true 语义）.
    /// </summary>
    [Fact]
    public void Parse_BadDocumentLine_FailsSingleItemOnly()
    {
        const string ndjson = """
            {"index":{"_index":"logs-1","_id":"1"}}
            {"message":"good"}
            {"index":{"_index":"logs-1","_id":"2"}}
            {broken json
            {"index":{"_index":"logs-1","_id":"3"}}
            {"message":"also good"}
            """;

        var result = EsBulkParser.Parse(ndjson);

        Assert.Equal(3, result.Items.Count);
        Assert.True(result.Items[0].Success);
        Assert.False(result.Items[1].Success);
        Assert.Equal("parse_exception", result.Items[1].ErrorType);
        Assert.True(result.Items[2].Success);
        Assert.True(result.HasFailure);
    }

    /// <summary>
    /// 坏动作行与非 JSON 文档形态：单项失败.
    /// </summary>
    [Fact]
    public void Parse_BadActionLine_FailsSingleItem()
    {
        const string ndjson = """
            not-json-at-all
            {"message":"orphan doc skipped as next meta? no"}
            """;

        var result = EsBulkParser.Parse(ndjson);

        Assert.Single(result.Items);
        Assert.False(result.Items[0].Success);
        Assert.Equal("parse_exception", result.Items[0].ErrorType);
    }

    /// <summary>
    /// 缺 _index：URL 索引兜底；无 URL 索引时单项失败.
    /// </summary>
    [Fact]
    public void Parse_MissingIndex_UrlFallbackOrFail()
    {
        const string withUrl = """
            {"index":{"_id":"1"}}
            {"message":"m"}
            """;
        const string withoutUrl = """
            {"index":{"_id":"1"}}
            {"message":"m"}
            """;

        var fallback = EsBulkParser.Parse(withUrl, "logs-from-url");
        Assert.True(fallback.Items[0].Success);
        Assert.Equal("logs-from-url", fallback.Items[0].Record!.Service);

        var failed = EsBulkParser.Parse(withoutUrl);
        Assert.False(failed.Items[0].Success);
        Assert.Equal("illegal_argument_exception", failed.Items[0].ErrorType);
    }

    /// <summary>
    /// 级别缺省 INFO；severity/level 顶层字段可用.
    /// </summary>
    [Fact]
    public void Parse_SeverityDefaultsToInfo()
    {
        const string ndjson = """
            {"index":{"_index":"logs-1"}}
            {"message":"no level"}
            {"index":{"_index":"logs-1"}}
            {"level":"error","message":"plain level"}
            """;

        var result = EsBulkParser.Parse(ndjson);

        Assert.Equal("INFO", result.Items[0].Record!.Severity);
        Assert.Equal("ERROR", result.Items[1].Record!.Severity);
    }

    /// <summary>
    /// 429 背压语义：任一条缓冲满则整批拒绝信号 + 429 响应体形状.
    /// </summary>
    [Fact]
    public void EnqueueAll_BufferFull_ReturnsRejectSignal()
    {
        var result = EsBulkParser.Parse(
            """
            {"index":{"_index":"logs-1"}}
            {"message":"a"}
            {"index":{"_index":"logs-1"}}
            {"message":"b"}
            """);
        Assert.All(result.Items, i => Assert.True(i.Success));

        var counter = 0;
        var accepted = EsBulkParser.EnqueueAll(result, _ =>
        {
            counter++;
            return counter < 2; // 第二条缓冲满
        });

        Assert.False(accepted);
        Assert.Equal(2, counter); // 整批拒绝前逐条尝试

        var bufferFull = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(EsBulkResponse.BufferFull()));
        Assert.Equal(429, bufferFull.GetProperty("status").GetInt32());
        Assert.Equal("es_rejected_execution_exception", bufferFull.GetProperty("error").GetProperty("type").GetString());
    }

    /// <summary>
    /// 全部缓冲正常：返回接受信号.
    /// </summary>
    [Fact]
    public void EnqueueAll_AllAccepted_ReturnsTrue()
    {
        var result = EsBulkParser.Parse(
            """
            {"index":{"_index":"logs-1"}}
            {"message":"a"}
            """);

        var accepted = EsBulkParser.EnqueueAll(result, _ => true);

        Assert.True(accepted);
    }
}
