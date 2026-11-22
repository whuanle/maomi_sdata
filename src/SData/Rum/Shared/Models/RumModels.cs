namespace SData.Rum.Models;

/// <summary>
/// RUM 上报配置响应（GET /rum/v1/config）.
/// </summary>
public class RumConfigResponse
{
    /// <summary>
    /// 性能事件采样率（0–1）.
    /// </summary>
    public double PerfSampleRate { get; set; } = 1.0;

    /// <summary>
    /// 会话回放采样率（0–1）.
    /// </summary>
    public double ReplaySampleRate { get; set; } = 1.0;
}

/// <summary>
/// RUM beacon 请求（C3 协议，gzip JSON）.
/// </summary>
public class RumBeaconRequest
{
    /// <summary>
    /// 项目标识.
    /// </summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>
    /// 会话标识.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// 事件数组.
    /// </summary>
    public List<RumEventPayload> Events { get; set; } = new();
}

/// <summary>
/// 实时点击事件（SignalR hub 推送与探针直连上报共用，T616）.
/// </summary>
public class RumLiveClickEvent
{
    /// <summary>
    /// 项目标识.
    /// </summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>
    /// 会话标识.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// 页面路径.
    /// </summary>
    public string? PagePath { get; set; }

    /// <summary>
    /// 视口相对坐标 X（clientX，向后兼容）.
    /// </summary>
    public double? X { get; set; }

    /// <summary>
    /// 视口相对坐标 Y（clientY，向后兼容）.
    /// </summary>
    public double? Y { get; set; }

    /// <summary>
    /// 文档绝对坐标 X（clientX + scrollX，滚动页面不位移）.
    /// </summary>
    public double? PageX { get; set; }

    /// <summary>
    /// 文档绝对坐标 Y（clientY + scrollY）.
    /// </summary>
    public double? PageY { get; set; }

    /// <summary>
    /// 文档总宽（scrollWidth，归一化分母）.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("docW")]
    public double? DocWidth { get; set; }

    /// <summary>
    /// 文档总高（scrollHeight，归一化分母）.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("docH")]
    public double? DocHeight { get; set; }

    /// <summary>
    /// 点击目标摘要（tag#id.class 短链）.
    /// </summary>
    public string? Selector { get; set; }

    /// <summary>
    /// 事件时间.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// 转 rum_event 落库记录（payload 键与探针 extra 形态一致：x/y/pageX/pageY/docW/docH/selector）.
    /// </summary>
    /// <returns>rum_event 记录.</returns>
    public SData.Store.Models.RumEventRecord ToRecord()
    {
        return new SData.Store.Models.RumEventRecord
        {
            ProjectKey = ProjectKey,
            SessionId = SessionId,
            EventType = "click",
            EventName = "click",
            PagePath = PagePath,
            Timestamp = Timestamp,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["x"] = X,
                ["y"] = Y,
                ["pageX"] = PageX,
                ["pageY"] = PageY,
                ["docW"] = DocWidth,
                ["docH"] = DocHeight,
                ["selector"] = Selector,
            }),
        };
    }
}

/// <summary>
/// RUM 事件载荷（C3：type=pageview|vital|error|click|custom|replay-chunk）.
/// </summary>
public class RumEventPayload
{
    /// <summary>
    /// 事件类型.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// 事件时间（ISO 8601，缺省 = 服务端接收时间）.
    /// </summary>
    public string? Ts { get; set; }

    /// <summary>
    /// 页面路径（pageview/click/custom）.
    /// </summary>
    public string? PagePath { get; set; }

    /// <summary>
    /// 数值（vital 指标值、click 坐标等）.
    /// </summary>
    public double? Value { get; set; }

    /// <summary>
    /// 事件名（vital 的 LCP/INP/CLS、custom 自定义名、error 的 message）.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 回放分片序号（replay-chunk）.
    /// </summary>
    public long? Seq { get; set; }

    /// <summary>
    /// 回放分片内容（gzip blob base64，replay-chunk）.
    /// </summary>
    public string? BlobBase64 { get; set; }

    /// <summary>
    /// 其余字段原样保留（序列化进 payload）.
    /// </summary>
    public Dictionary<string, object?>? Extra { get; set; }
}
