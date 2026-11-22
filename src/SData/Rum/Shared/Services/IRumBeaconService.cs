using SData.Rum.Models;

namespace SData.Rum;

/// <summary>
/// RUM beacon 摄取结果状态.
/// </summary>
public enum RumIngestStatus
{
    /// <summary>全部入缓冲.</summary>
    Ok,

    /// <summary>projectKey 缺失（HTTP 401）.</summary>
    InvalidKey,

    /// <summary>缓冲满（HTTP 429）.</summary>
    BufferFull,

    /// <summary>项目或来源不允许（HTTP 403）.</summary>
    Forbidden,
}

/// <summary>
/// RUM beacon 摄取接口（实现在 Core，Api 通过接口调用，保持分层单向）.
/// </summary>
public interface IRumBeaconService
{
    /// <summary>
    /// 摄取一条 beacon（gzip JSON 或明文 JSON）.
    /// </summary>
    /// <param name="body">请求体.</param>
    /// <param name="contentEncoding">Content-Encoding（gzip 或空）.</param>
    /// <param name="origin">从请求头解析的来源.</param>
    /// <returns>摄取状态.</returns>
    Task<RumIngestStatus> IngestAsync(byte[] body, string? contentEncoding, string? origin = null);

    /// <summary>
    /// C3 采样配置：仅向项目允许来源返回保存值，否则返回 null。
    /// </summary>
    /// <param name="projectKey">项目标识.</param>
    /// <param name="origin">从请求头解析的来源.</param>
    /// <returns>配置.</returns>
    RumConfigResponse? GetConfig(string projectKey, string? origin = null);
}
