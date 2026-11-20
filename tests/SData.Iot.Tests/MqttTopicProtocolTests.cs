using System.Text;
using SData.Iot.Mqtt;
using Xunit;

namespace SData.Iot.Tests;

/// <summary>
/// MQTT 主题与消息体协议解析测试（单值与批量 JSON 两形态）.
/// </summary>
public class MqttTopicProtocolTests
{
    /// <summary>
    /// 合法主题：iot/{deviceKey}/{signal}，信号名支持多级.
    /// </summary>
    [Theory]
    [InlineData("iot/dev1/temperature", "dev1", "temperature")]
    [InlineData("iot/dev1/line1/pressure", "dev1", "line1/pressure")]
    public void TryParseTopic_Valid_ReturnsDeviceAndSignal(string topic, string expectedDevice, string expectedSignal)
    {
        var ok = MqttTopicProtocol.TryParseTopic(topic, out var deviceKey, out var signal);

        Assert.True(ok);
        Assert.Equal(expectedDevice, deviceKey);
        Assert.Equal(expectedSignal, signal);
    }

    /// <summary>
    /// 非法主题：空、缺前缀、段数不足、设备/信号为空.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("logs/dev1/t1")]
    [InlineData("iot/dev1")]
    [InlineData("iot//temperature")]
    [InlineData("iot/dev1/")]
    public void TryParseTopic_Invalid_ReturnsFalse(string topic)
    {
        var ok = MqttTopicProtocol.TryParseTopic(topic, out _, out _);

        Assert.False(ok);
    }

    /// <summary>
    /// 单值形态：JSON 对象 v 数值 + ts + q 全量解析.
    /// </summary>
    [Fact]
    public void TryParsePayload_SingleObject_ParsesValueTimestampQuality()
    {
        var ok = MqttTopicProtocol.TryParsePayload("iot/dev1/temperature", Encoding.UTF8.GetBytes(
                """{"v": 23.5, "ts": "2026-11-15T10:00:00+08:00", "q": 1}"""),
            DateTimeOffset.Parse("2026-11-15T12:00:00+08:00"), out var records, out var error);

        Assert.True(ok, error);
        var record = Assert.Single(records);
        Assert.Equal("dev1", record.DeviceKey);
        Assert.Equal("temperature", record.Signal);
        Assert.Equal(23.5, record.NumValue);
        Assert.Null(record.StrValue);
        Assert.Equal(1, record.Quality);
        Assert.Equal(DateTimeOffset.Parse("2026-11-15T10:00:00+08:00"), record.Timestamp);
    }

    /// <summary>
    /// 单值形态：字符串值进 StrValue，时间缺省取接收时间.
    /// </summary>
    [Fact]
    public void TryParsePayload_SingleObject_StringValue_UsesFallbackTime()
    {
        var fallback = DateTimeOffset.Parse("2026-11-15T12:00:00+08:00");

        var ok = MqttTopicProtocol.TryParsePayload("iot/dev1/state", Encoding.UTF8.GetBytes("""{"v": "RUN"}"""),
            fallback, out var records, out var error);

        Assert.True(ok, error);
        var record = Assert.Single(records);
        Assert.Null(record.NumValue);
        Assert.Equal("RUN", record.StrValue);
        Assert.Equal(0, record.Quality);
        Assert.Equal(fallback, record.Timestamp);
    }

    /// <summary>
    /// 单值形态：JSON 标量数字与 JSON 字符串消息体（裸字符串不是合法 JSON，按非法消息体拒绝）.
    /// </summary>
    [Fact]
    public void TryParsePayload_ScalarPayload_ParsesByKind()
    {
        var fallback = DateTimeOffset.FromUnixTimeMilliseconds(1_741_000_000_000);
        var ok1 = MqttTopicProtocol.TryParsePayload("iot/dev1/count", Encoding.UTF8.GetBytes("42"), fallback, out var records1, out var error1);
        var ok2 = MqttTopicProtocol.TryParsePayload("iot/dev1/mode", Encoding.UTF8.GetBytes("\"idle\""), fallback, out var records2, out var error2);
        var bad = MqttTopicProtocol.TryParsePayload("iot/dev1/mode", Encoding.UTF8.GetBytes("idle"), fallback, out _, out var errorBad);

        Assert.True(ok1, error1);
        Assert.True(ok2, error2);
        Assert.Equal(42d, Assert.Single(records1).NumValue);
        Assert.Equal("idle", Assert.Single(records2).StrValue);
        Assert.False(bad);
        Assert.False(string.IsNullOrEmpty(errorBad));
    }

    /// <summary>
    /// 批量形态：JSON 数组元素自带 s/signal，支持 ts epoch 毫秒与 quality.
    /// </summary>
    [Fact]
    public void TryParsePayload_BatchArray_ParsesAllEntries()
    {
        var ok = MqttTopicProtocol.TryParsePayload("iot/dev1/batch", Encoding.UTF8.GetBytes(
                """[{"s": "t1", "v": 1.5}, {"signal": "t2", "value": "OFF", "ts": 1741998600000, "q": 2}]"""),
            DateTimeOffset.Parse("2026-11-15T12:00:00+08:00"), out var records, out var error);

        Assert.True(ok, error);
        Assert.Equal(2, records.Count);
        Assert.Equal("t1", records[0].Signal);
        Assert.Equal(1.5, records[0].NumValue);
        Assert.Equal("t2", records[1].Signal);
        Assert.Equal("OFF", records[1].StrValue);
        Assert.Equal(2, records[1].Quality);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_741_998_600_000), records[1].Timestamp);
    }

    /// <summary>
    /// 失败语义：非法 JSON、批量缺 s、空消息体、缺 v、非法时间戳.
    /// </summary>
    [Theory]
    [InlineData("iot/dev1/t1", "not-json")]
    [InlineData("iot/dev1/t1", "[]")]
    [InlineData("iot/dev1/t1", "[{\"v\": 1}]")]
    [InlineData("iot/dev1/t1", "{}")]
    [InlineData("iot/dev1/t1", "{\"ts\": \"bad-time\", \"v\": 1}")]
    public void TryParsePayload_Invalid_ReturnsFalseWithError(string topic, string payload)
    {
        var ok = MqttTopicProtocol.TryParsePayload(topic, Encoding.UTF8.GetBytes(payload),
            DateTimeOffset.UtcNow, out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
