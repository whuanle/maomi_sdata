using SData.Infra.Configuration;

namespace SData.Settings;

/// <summary>
/// MQ 日志解析预览器（接口在 Settings.Shared，实现经 Ingest.Core 的 MqLogDecoder；跨域经接口依赖）.
/// 供解析规则配置页调试：样例文本按规则试解析，返回映射效果，不落库不进摄取缓冲.
/// </summary>
public interface IMqLogPreviewer
{
    /// <summary>
    /// 按给定 topic 解析规则试解析样例文本.
    /// </summary>
    /// <param name="sample">样例日志文本（可多行）.</param>
    /// <param name="topic">topic 解析规则（Name/Format/Service/Severity/Parser）.</param>
    /// <returns>解析结果（失败行含原文入库与 _parse_error 标记）.</returns>
    MqParsePreviewResult Preview(string sample, MqTopicOptions topic);
}
