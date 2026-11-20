using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using SData.Infra.Configuration;

namespace SData.Ingest.Api;

/// <summary>
/// Elasticsearch _bulk 写入兼容端点（Filebeat/Logstash 直推）：
/// NDJSON index/create 动作落日志缓冲，缓冲满整批 429 背压.
/// </summary>
[ApiController]
public class EsBulkController : ControllerBase
{
    private readonly IIngestIngestor _ingestor;
    private readonly IEsBulkParser _parser;
    private readonly IIngestKeyValidator _keyValidator;
    private readonly SystemOptions _systemOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="EsBulkController"/> class.
    /// </summary>
    /// <param name="ingestor">接入服务.</param>
    /// <param name="parser">ES Bulk 解析器.</param>
    /// <param name="keyValidator">接入 key 校验器.</param>
    /// <param name="systemOptions">系统配置.</param>
    public EsBulkController(IIngestIngestor ingestor, IEsBulkParser parser, IIngestKeyValidator keyValidator, SystemOptions systemOptions)
    {
        _ingestor = ingestor;
        _parser = parser;
        _keyValidator = keyValidator;
        _systemOptions = systemOptions;
    }

    /// <summary>
    /// ES _bulk 批量写入（POST /_bulk 或 POST /&#123;index&#125;/_bulk，NDJSON）.
    /// </summary>
    /// <param name="index">URL 中的默认索引（动作行缺 _index 时兜底）.</param>
    /// <returns>ES 形状响应.</returns>
    [HttpPost("/_bulk")]
    [HttpPost("/{index}/_bulk")]
    public async Task<IActionResult> Bulk(string? index)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!await AuthorizeAsync())
        {
            return Unauthorized(EsSecurityError());
        }

        string ndjson;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            ndjson = await reader.ReadToEndAsync();
        }

        var result = _parser.Parse(ndjson, index);

        // 缓冲满整批 429：Filebeat 收到后自动重试
        if (!_parser.EnqueueAll(result, _ingestor.EnqueueLog))
        {
            return StatusCode(429, EsBulkResponse.BufferFull());
        }

        var response = new EsBulkResponse
        {
            Took = stopwatch.ElapsedMilliseconds,
            Errors = result.HasFailure,
        };
        foreach (var item in result.Items)
        {
            response.Items.Add(item.Success
                ? EsBulkResponse.SuccessItem(item.Action, item.Index, item.DocumentId)
                : EsBulkResponse.ErrorItem(item.Action, item.Index, item.DocumentId, item.ErrorType ?? "illegal_argument_exception", item.ErrorReason ?? "解析失败"));
        }

        return Ok(response);
    }

    private Task<bool> AuthorizeAsync()
        => _keyValidator.ValidateAsync(IngestApiKeyFilter.ExtractApiKey(Request.Headers) ?? string.Empty);

    private static string? ExtractBasicPassword(string authorization)
    {
        try
        {
            var encoded = authorization["Basic ".Length..].Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var separator = decoded.IndexOf(':');
            return separator >= 0 ? decoded[(separator + 1)..] : decoded;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static object EsSecurityError()
    {
        return new
        {
            error = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["root_cause"] = Array.Empty<object>(),
                ["type"] = "security_exception",
                ["reason"] = "接入鉴权失败，请提供有效的 X-Api-Key / Bearer / Basic(password=接入 Key)",
            },
            status = 401,
        };
    }
}
