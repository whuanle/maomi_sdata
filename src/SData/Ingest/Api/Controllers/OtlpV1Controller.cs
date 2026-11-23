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
[IngestBackpressure]
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
        return RunAsync(Request, _protoParser.ParseLogs, _parser.ParseLogs, _ingestor.EnqueueLogs);
    }

    /// <summary>
    /// OTLP traces 上报（proto/JSON 双格式）.
    /// </summary>
    /// <returns>OTLP 响应.</returns>
    [HttpPost("traces")]
    public Task<IActionResult> Traces()
    {
        return RunAsync(Request, _protoParser.ParseTraces, _parser.ParseTraces, _ingestor.EnqueueSpans);
    }

    /// <summary>
    /// OTLP metrics 上报（proto/JSON 双格式）.
    /// </summary>
    /// <returns>OTLP 响应.</returns>
    [HttpPost("metrics")]
    public Task<IActionResult> Metrics()
    {
        return RunAsync(Request, _protoParser.ParseMetrics, _parser.ParseMetrics, _ingestor.EnqueueMetrics);
    }

    private Task<IActionResult> RunAsync<T>(
        HttpRequest request,
        Func<byte[], int, int, List<T>> parseProto,
        Func<string, List<T>> parseJson,
        Func<IReadOnlyList<T>, (long Accepted, long Dropped)> enqueueBatch)
    {
        return ExportAsync(request, parseProto, parseJson, enqueueBatch, _ingestor.CanAcceptBytes);
    }

    /// <summary>
    /// 共享导出处理链：读体（gzip 解压）→ 按协议格式解析 → 批量入缓冲 → OTLP 响应形状.
    /// 旧 IngestController 的 /ingest/otlp/v1/* 端点经自身注入的解析器转调本方法保持行为一致.
    /// </summary>
    /// <typeparam name="T">领域记录类型.</typeparam>
    /// <param name="request">HTTP 请求.</param>
    /// <param name="parseProto">proto 字节解析（已解压，缓冲区切片免整包拷贝）.</param>
    /// <param name="parseJson">JSON 文本解析.</param>
    /// <param name="enqueueBatch">批量入缓冲（返回接受/丢弃条数）.</param>
    /// <param name="canAcceptBytes">解析前字节预算预检（null 跳过）.</param>
    /// <returns>OTLP 响应.</returns>
    internal static async Task<IActionResult> ExportAsync<T>(
        HttpRequest request,
        Func<byte[], int, int, List<T>> parseProto,
        Func<string, List<T>> parseJson,
        Func<IReadOnlyList<T>, (long Accepted, long Dropped)> enqueueBatch,
        Func<long, bool>? canAcceptBytes = null)
    {
        List<T> records;
        try
        {
            var isProto = (request.ContentType ?? string.Empty).Contains("x-protobuf", StringComparison.OrdinalIgnoreCase);
            var gzipped = IsGzip(request.Headers.ContentEncoding);
            using var payload = new MemoryStream();
            if (gzipped)
            {
                using var gzip = new GZipStream(request.Body, CompressionMode.Decompress, leaveOpen: true);
                await gzip.CopyToAsync(payload, request.HttpContext.RequestAborted);
            }
            else
            {
                await request.Body.CopyToAsync(payload, request.HttpContext.RequestAborted);
            }

            if (canAcceptBytes != null && payload.Length > 0 && !canAcceptBytes(payload.Length))
            {
                return BufferFull(request.HttpContext);
            }

            if (isProto)
            {
                // 直接在 MemoryStream 内部缓冲上切片解析，省去整包 ToArray 拷贝
                records = parseProto(payload.GetBuffer(), 0, checked((int)payload.Length));
            }
            else
            {
                // JSON parser consumes text; decode directly from the memory stream buffer without an intermediate byte[] copy.
                records = parseJson(System.Text.Encoding.UTF8.GetString(payload.GetBuffer(), 0, checked((int)payload.Length)));
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            return new BadRequestObjectResult(new { message = "OTLP 报文解析失败，请检查协议格式与 Content-Type/Content-Encoding" });
        }

        // 批量入缓冲：部分接受（dropped>0）时返回 429 并在 partialSuccess 中提示
        var (_, dropped) = enqueueBatch(records);
        if (dropped > 0)
        {
            request.HttpContext.Response.Headers["Retry-After"] = "1";
            return new ObjectResult(new { partialSuccess = new { errorMessage = "buffer full, data dropped" } })
            {
                StatusCode = 429,
            };
        }

        return new OkObjectResult(new { partialSuccess = (object?)null });
    }

    private static ObjectResult BufferFull(HttpContext httpContext)
    {
        httpContext.Response.Headers["Retry-After"] = "1";
        return new ObjectResult(new { partialSuccess = new { errorMessage = "buffer full, data dropped" } })
        {
            StatusCode = 429,
        };
    }

    private static bool IsGzip(Microsoft.Extensions.Primitives.StringValues contentEncoding)
    {
        return contentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase);
    }
}
