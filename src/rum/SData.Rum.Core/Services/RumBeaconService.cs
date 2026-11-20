using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SData.Database;
using SData.Rum.Models;
using SData.Store;
using SData.Store.Models;

namespace SData.Rum.Services;

/// <summary>
/// RUM beacon 摄取服务（C3 协议：gzip JSON → 事件分派入缓冲）.
/// projectKey 缺失返回 401；项目和来源必须通过严格白名单校验。
/// </summary>
public class RumBeaconService : IRumBeaconService
{
    private readonly IIngestBuffer _buffer;
    private readonly IRumOriginPolicy? _originPolicy;
    private readonly IRumLivePublisher? _livePublisher;
    private readonly IServiceScopeFactory? _scopeFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="RumBeaconService"/> class.
    /// </summary>
    /// <param name="buffer">接入内存缓冲.</param>
    /// <param name="livePublisher">实时点击发布器（可选；测试场景可为空）.</param>
    public RumBeaconService(IIngestBuffer buffer, IRumLivePublisher? livePublisher = null, IServiceScopeFactory? scopeFactory = null, IRumOriginPolicy? originPolicy = null)
    {
        _buffer = buffer;
        _originPolicy = originPolicy;
        _livePublisher = livePublisher;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// 摄取一条 beacon（gzip JSON 或明文 JSON）.
    /// </summary>
    /// <param name="body">请求体.</param>
    /// <param name="contentEncoding">Content-Encoding（gzip 或空）.</param>
    /// <returns>摄取状态.</returns>
    /// <inheritdoc/>
    public async Task<RumIngestStatus> IngestAsync(byte[] body, string? contentEncoding, string? origin = null)
    {
        var json = Decode(body, contentEncoding);
        if (string.IsNullOrWhiteSpace(json))
        {
            return RumIngestStatus.InvalidKey;
        }

        RumBeaconRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<RumBeaconRequest>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return RumIngestStatus.InvalidKey;
        }

        if (request == null || string.IsNullOrEmpty(request.ProjectKey))
        {
            return RumIngestStatus.InvalidKey;
        }

        if (_originPolicy?.Allows(request.ProjectKey, origin) != true)
        {
            return RumIngestStatus.Forbidden;
        }

        var events = request.Events ?? new List<RumEventPayload>();
        var accepted = true;
        foreach (var payload in events)
        {
            accepted = Dispatch(request, payload) && accepted;
        }

        return accepted ? RumIngestStatus.Ok : RumIngestStatus.BufferFull;
    }

    /// <summary>
    /// C3 采样配置：仅向项目允许来源返回保存的采样率。
    /// </summary>
    /// <param name="projectKey">项目标识.</param>
    /// <returns>配置.</returns>
    /// <inheritdoc/>
    public RumConfigResponse? GetConfig(string projectKey, string? origin = null)
    {
        if (_originPolicy?.Allows(projectKey, origin) != true)
        {
            return null;
        }

        if (_scopeFactory != null)
        {
            using var scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            var project = database.RumProjects.AsNoTracking().FirstOrDefault(x => x.ProjectKey == projectKey);
            if (project != null)
            {
                return new RumConfigResponse { PerfSampleRate = project.PerfSampleRate, ReplaySampleRate = project.ReplaySampleRate };
            }
        }

        return null;
    }

    private bool Dispatch(RumBeaconRequest request, RumEventPayload payload)
    {
        var timestamp = ParseTimestamp(payload.Ts) ?? DateTimeOffset.UtcNow;
        switch (payload.Type)
        {
            case "replay-chunk":
                return _buffer.TryEnqueueRumReplay(new RumReplayRecord
                {
                    ProjectKey = request.ProjectKey,
                    SessionId = request.SessionId,
                    Timestamp = timestamp,
                    Seq = payload.Seq ?? 0,
                    BlobBase64 = payload.BlobBase64 ?? string.Empty,
                });
            case "pageview":
                return _buffer.TryEnqueueRumSession(new RumSessionRecord
                {
                    ProjectKey = request.ProjectKey,
                    SessionId = request.SessionId,
                    Timestamp = timestamp,
                    UserAgent = ExtraString(payload, "ua"),
                    Domain = ExtraString(payload, "domain"),
                });
            case "vital":
            case "error":
            case "click":
            case "custom":
            {
                var record = new RumEventRecord
                {
                    ProjectKey = request.ProjectKey,
                    SessionId = request.SessionId,
                    EventType = payload.Type,
                    PagePath = payload.PagePath,
                    EventName = payload.Name,
                    Value = payload.Value,
                    Timestamp = timestamp,
                    PayloadJson = payload.Extra == null ? null : JsonSerializer.Serialize(payload.Extra),
                };
                var accepted = _buffer.TryEnqueueRumEvent(record);
                if (accepted && payload.Type == "click")
                {
                    // beacon 批量通道的点击同样实时广播（T616）
                    _livePublisher?.PublishClick(record, BuildLiveClick(request, payload, timestamp));
                }

                return accepted;
            }
            default:
                // 未知类型按 custom 事件落 rum_event（宽容模式）
                return _buffer.TryEnqueueRumEvent(new RumEventRecord
                {
                    ProjectKey = request.ProjectKey,
                    SessionId = request.SessionId,
                    EventType = "custom",
                    PagePath = payload.PagePath,
                    EventName = payload.Name,
                    Value = payload.Value,
                    Timestamp = timestamp,
                    PayloadJson = payload.Extra == null ? JsonSerializer.Serialize(payload) : JsonSerializer.Serialize(payload.Extra),
                });
        }
    }

    private static string? ExtraString(RumEventPayload payload, string key)
    {
        return payload.Extra != null && payload.Extra.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    /// <summary>
    /// 从 extra 提取数值（System.Text.Json 反序列化产物为 JsonElement）.
    /// </summary>
    private static double? ExtraDouble(RumEventPayload payload, string key)
    {
        if (payload.Extra == null || !payload.Extra.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Number ? element.GetDouble() : null;
        }

        return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 由 extra 组装实时点击载荷（x/y 视口坐标 + pageX/pageY 文档绝对坐标，T616）.
    /// </summary>
    private static RumLiveClickEvent BuildLiveClick(RumBeaconRequest request, RumEventPayload payload, DateTimeOffset timestamp)
    {
        return new RumLiveClickEvent
        {
            ProjectKey = request.ProjectKey,
            SessionId = request.SessionId,
            PagePath = payload.PagePath,
            X = ExtraDouble(payload, "x"),
            Y = ExtraDouble(payload, "y"),
            PageX = ExtraDouble(payload, "pageX"),
            PageY = ExtraDouble(payload, "pageY"),
            DocWidth = ExtraDouble(payload, "docW"),
            DocHeight = ExtraDouble(payload, "docH"),
            Selector = ExtraString(payload, "selector"),
            Timestamp = timestamp,
        };
    }

    private static DateTimeOffset? ParseTimestamp(string? ts)
    {
        return DateTimeOffset.TryParse(ts, out var parsed) ? parsed : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static string Decode(byte[] body, string? contentEncoding)
    {
        if (!string.Equals(contentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8.GetString(body);
        }

        using var input = new MemoryStream(body);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
