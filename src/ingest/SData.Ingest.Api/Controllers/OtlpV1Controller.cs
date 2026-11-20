using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace SData.Ingest.Api;

/// <summary>
/// OTLP 标准接收器（OTLP/HTTP）：/v1/logs|traces|metrics，application/x-protobuf 与 JSON 双格式，支持 gzip 请求体.
/// 旧路径 /ingest/otlp/v1/*（IngestController）转发至共享处理链，保留一个版本周期后废弃.
/// </summary>
[ApiController]
[Route("/v1")]
[Route("/api/v1")]
[ServiceFilter(typeof(IngestApiKeyFilter))]
public class OtlpV1Controller : ControllerBase
{
    private readonly SData.Ingest.IIngestIngestor _ingestor;
    private readonly SData.Ingest.IOtlpParser _parser;
    private readonly SData.Ingest.IOtlpProtoParser _protoParser;

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpV1Controller"/> class.
    /// </summary>
    /// <param name="ingestor">接入服务.</param>
    /// <param name="parser">OTLP JSON 解析器.</param>
    /// <param name="protoParser">OTLP proto 解析器.</param>
    public OtlpV1Controller(SData.Ingest.IIngestIngestor ingestor, SData.Ingest.IOtlpParser parser, SData.Ingest.IOtlpProtoParser protoParser)
    {
        _ingestor = ingestor;
        _parser = parser;
        _protoParser = protoParser;
    }

    /// <summary>
    /// OTLP logs 上报（proto/JSON 双格式）.
    /// </summary>
    /// <returns>OTLP 响应.</returns>
    [HttpPost("logs")]
    public Task<IActionResult> Logs()
    {
        return RunAsync(Request, _protoParser.ParseLogs, _parser.ParseLogs, _ingestor.EnqueueLog);
    }

    /// <summary>
    /// OTLP traces 上报（proto/JSON 双格式）.
    /// </summary>
    /// <returns>OTLP 响应.</returns>
    [HttpPost("traces")]
    public Task<IActionResult> Traces()
    {
        return RunAsync(Request, _protoParser.ParseTraces, _parser.ParseTraces, _ingestor.EnqueueSpan);
    }

    /// <summary>
    /// OTLP metrics 上报（proto/JSON 双格式）.
    /// </summary>
    /// <returns>OTLP 响应.</returns>
    [HttpPost("metrics")]
    public Task<IActionResult> Metrics()
    {
        return RunAsync(Request, _protoParser.ParseMetrics, _parser.ParseMetrics, _ingestor.EnqueueMetric);
    }

    private async Task<IActionResult> RunAsync<T>(
        HttpRequest request,
        Func<byte[], List<T>> parseProto,
        Func<string, List<T>> parseJson,
        Func<T, bool> enqueue)
    {
        return await ExportAsync(request, parseProto, parseJson, enqueue);
    }

    /// <summary>
    /// 共享导出处理链：读体（gzip 解压）→ 按协议格式解析 → 逐条入缓冲 → OTLP 响应形状.
    /// 旧 IngestController 的 /ingest/otlp/v1/* 端点经自身注入的解析器转调本方法保持行为一致.
    /// </summary>
    /// <typeparam name="T">领域记录类型.</typeparam>
    /// <param name="request">HTTP 请求.</param>
    /// <param name="parseProto">proto 字节解析（已解压）.</param>
    /// <param name="parseJson">JSON 文本解析.</param>
    /// <param name="enqueue">单条入缓冲（false = 缓冲满）.</param>
    /// <returns>OTLP 响应.</returns>
    internal static async Task<IActionResult> ExportAsync<T>(
        HttpRequest request,
        Func<byte[], List<T>> parseProto,
        Func<string, List<T>> parseJson,
        Func<T, bool> enqueue)
    {
        List<T> records;
        try
        {
            var isProto = (request.ContentType ?? string.Empty).Contains("x-protobuf", StringComparison.OrdinalIgnoreCase);
            var gzipped = IsGzip(request.Headers.ContentEncoding);
            if (isProto)
            {
                using var raw = new MemoryStream();
                await request.Body.CopyToAsync(raw);
                var bytes = gzipped ? Decompress(raw.ToArray()) : raw.ToArray();
                records = parseProto(bytes);
            }
            else
            {
                // JSON 分支同样按原始字节读取：gzip 压缩流是二进制，经 StreamReader 文本解码会被破坏
                using var raw = new MemoryStream();
                await request.Body.CopyToAsync(raw);
                var payload = gzipped ? Decompress(raw.ToArray()) : raw.ToArray();
                var text = System.Text.Encoding.UTF8.GetString(payload);
                records = parseJson(text);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            return new BadRequestObjectResult(new { message = "OTLP 报文解析失败，请检查协议格式与 Content-Type/Content-Encoding" });
        }

        var accepted = 1;
        foreach (var record in records)
        {
            if (!enqueue(record))
            {
                accepted = -1;
                break;
            }
        }

        // 接入端点只做内存缓冲，缓冲满时返回 429 并在 partialSuccess 中提示
        if (accepted < 0)
        {
            return new ObjectResult(new { partialSuccess = new { errorMessage = "buffer full, data dropped" } })
            {
                StatusCode = 429,
            };
        }

        return new OkObjectResult(new { partialSuccess = (object?)null });
    }

    private static byte[] Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static bool IsGzip(Microsoft.Extensions.Primitives.StringValues contentEncoding)
    {
        return contentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase);
    }
}
