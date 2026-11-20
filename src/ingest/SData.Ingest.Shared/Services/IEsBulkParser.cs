using SData.Store.Models;

namespace SData.Ingest;

/// <summary>
/// ES Bulk NDJSON 解析接口（实现在 Core，Api 通过接口调用，保持分层单向）.
/// </summary>
public interface IEsBulkParser
{
    /// <summary>
    /// 解析 NDJSON 请求体（动作行 + 文档行成对，delete 动作无文档行）.
    /// </summary>
    /// <param name="ndjson">请求体原文.</param>
    /// <param name="urlIndex">URL /&#123;index&#125;/_bulk 中的默认索引.</param>
    /// <returns>解析结果.</returns>
    EsBulkParseResult Parse(string ndjson, string? urlIndex = null);

    /// <summary>
    /// 逐条入缓冲（429 背压决策：任一条缓冲满即整批拒绝）.
    /// </summary>
    /// <param name="result">解析结果.</param>
    /// <param name="enqueue">单条入缓冲委托（false = 缓冲满）.</param>
    /// <returns>false = 任一条缓冲满，整批应拒绝（HTTP 429）.</returns>
    bool EnqueueAll(EsBulkParseResult result, Func<LogRecord, bool> enqueue);
}
