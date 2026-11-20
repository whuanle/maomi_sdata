using SData.Infra.Configuration;
using SData.Ingest.Consumers;
using Xunit;

namespace SData.Ingest.Tests;

/// <summary>
/// MQ 消费管理器协调逻辑测试：期望集过滤（启用/连接/topic/类型）与指纹稳定性（指纹变化即触发重启）.
/// </summary>
public class MqConsumerManagerTests
{
    /// <summary>
    /// 已启用且连接/topic 齐备的实例进入期望集；禁用、缺连接、缺 topic、未知类型被过滤.
    /// </summary>
    [Fact]
    public void ComputeDesired_FiltersNonRunnableInstances()
    {
        var instances = new List<MqInstanceOptions>
        {
            new() { Id = 1, Name = "k1", Type = "kafka", Enabled = true, BootstrapServers = "b:9092", GroupId = "g", Topics = [new MqTopicOptions { Name = "t1", Format = MqLogFormat.Raw }] },
            new() { Id = 2, Name = "k2", Type = "kafka", Enabled = false, BootstrapServers = "b:9092", Topics = [new MqTopicOptions { Name = "t", Format = MqLogFormat.Raw }] },
            new() { Id = 3, Name = "k3", Type = "kafka", Enabled = true, BootstrapServers = "", Topics = [new MqTopicOptions { Name = "t", Format = MqLogFormat.Raw }] },
            new() { Id = 4, Name = "k4", Type = "kafka", Enabled = true, BootstrapServers = "b:9092", Topics = [] },
            new() { Id = 5, Name = "k5", Type = "redis", Enabled = true, BootstrapServers = "b:9092", Topics = [new MqTopicOptions { Name = "t", Format = MqLogFormat.Raw }] },
            new() { Id = 6, Name = "r1", Type = "rabbitmq", Enabled = true, Uri = "amqp://guest:guest@localhost:5672/", Topics = [new MqTopicOptions { Name = "q", Format = MqLogFormat.Json }] },
            new() { Id = 7, Name = "p1", Type = "pulsar", Enabled = true, ServiceUrl = "pulsar://localhost:6650", SubscriptionName = "s", Topics = [new MqTopicOptions { Name = "persistent://public/default/x", Format = MqLogFormat.Regex }] },
        };

        var desired = MqConsumerManagerService.ComputeDesired(instances);

        Assert.Equal([1L, 6L, 7L], desired.Keys.OrderBy(k => k));
    }

    /// <summary>
    /// 配置未变指纹稳定；任何配置变化（如解析规则调整）都产生新指纹 → 触发消费者重启.
    /// </summary>
    [Fact]
    public void ComputeDesired_FingerprintStableUnlessConfigChanges()
    {
        var before = new MqInstanceOptions { Id = 1, Name = "k1", Type = "kafka", Enabled = true, BootstrapServers = "b:9092", GroupId = "g", Topics = [new MqTopicOptions { Name = "t1", Format = MqLogFormat.Raw }] };
        var unchanged = new MqInstanceOptions { Id = 1, Name = "k1", Type = "kafka", Enabled = true, BootstrapServers = "b:9092", GroupId = "g", Topics = [new MqTopicOptions { Name = "t1", Format = MqLogFormat.Raw }] };
        var changed = new MqInstanceOptions { Id = 1, Name = "k1", Type = "kafka", Enabled = true, BootstrapServers = "b:9092", GroupId = "g", Topics = [new MqTopicOptions { Name = "t1", Format = MqLogFormat.Regex, Parser = new MqParserOptions { Pattern = "(?<msg>.*)" } }] };

        var fpBefore = Assert.Single(MqConsumerManagerService.ComputeDesired([before])).Value.Fingerprint;
        var fpUnchanged = Assert.Single(MqConsumerManagerService.ComputeDesired([unchanged])).Value.Fingerprint;
        var fpChanged = Assert.Single(MqConsumerManagerService.ComputeDesired([changed])).Value.Fingerprint;

        Assert.Equal(fpBefore, fpUnchanged);
        Assert.NotEqual(fpBefore, fpChanged);
    }

    /// <summary>
    /// 解析规则不在白名单的 topic 不计入可运行判定.
    /// </summary>
    [Fact]
    public void IsRunnable_UnsupportedTopicFormat_NotRunnable()
    {
        var instance = new MqInstanceOptions
        {
            Id = 1,
            Name = "k1",
            Type = "kafka",
            Enabled = true,
            BootstrapServers = "b:9092",
            Topics = [new MqTopicOptions { Name = "t1", Format = "xml" }],
        };

        Assert.False(MqConsumerManagerService.IsRunnable(instance));
    }
}
