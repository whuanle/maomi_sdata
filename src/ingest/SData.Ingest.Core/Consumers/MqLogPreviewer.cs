using System.Text;
using SData.Infra.Configuration;
using SData.Settings;

namespace SData.Ingest.Consumers;

/// <summary>
/// MQ 日志解析预览器：委托 MqLogDecoder 试解析样例文本，映射为预览结果（不落库不进摄取缓冲）.
/// </summary>
public class MqLogPreviewer : IMqLogPreviewer
{
    /// <summary>预览返回的最大记录数（样例多行时截断，防止响应过大）.</summary>
    private const int MaxRecords = 50;

    private readonly IMqLogDecoder _decoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="MqLogPreviewer"/> class.
    /// </summary>
    /// <param name="decoder">消息解码.</param>
    public MqLogPreviewer(IMqLogDecoder decoder)
    {
        _decoder = decoder;
    }

    /// <inheritdoc/>
    public MqParsePreviewResult Preview(string sample, MqTopicOptions topic)
    {
        var result = new MqParsePreviewResult();
        var ok = _decoder.TryDecode(Encoding.UTF8.GetBytes(sample), topic, string.Empty, out var records, out var error);
        result.Error = error;
        result.Success = ok && error == null && records.Count > 0;
        if (!ok)
        {
            result.Error ??= "样例为空或解析规则未支持";
        }

        foreach (var record in records.Take(MaxRecords))
        {
            result.Records.Add(new MqLogPreviewRecord
            {
                Timestamp = record.Timestamp.ToString("O"),
                Severity = record.Severity,
                Service = record.Service,
                Body = record.Body,
                TraceId = record.TraceId,
                SpanId = record.SpanId,
                AttributesJson = record.AttributesJson,
            });
        }

        return result;
    }
}
