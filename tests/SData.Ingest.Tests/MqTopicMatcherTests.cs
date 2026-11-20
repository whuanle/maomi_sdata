using SData.Infra.Configuration;
using SData.Ingest.Consumers;
using Xunit;

namespace SData.Ingest.Tests;

/// <summary>主题规则的确定性选择.</summary>
public class MqTopicMatcherTests
{
    [Fact]
    public void ExactThenLongestPrefix()
    {
        var broad = new MqTopicOptions { Name = "logs-", MatchMode = "prefix" };
        var narrow = new MqTopicOptions { Name = "logs-api-", MatchMode = "prefix" };
        var exact = new MqTopicOptions { Name = "logs-api-prod" };
        var rules = new[] { broad, narrow, exact };
        Assert.Same(exact, MqTopicMatcher.Match(rules, "logs-api-prod"));
        Assert.Same(narrow, MqTopicMatcher.Match(rules, "logs-api-dev"));
        Assert.Same(broad, MqTopicMatcher.Match(rules, "logs-db"));
        Assert.Null(MqTopicMatcher.Match(rules, "other"));
        Assert.Null(MqTopicMatcher.Match(rules, "LOGS-db"));
    }

    [Fact]
    public void PrefixEscapesKafkaRegexAndKeepsActualSource()
    {
        var rule = new MqTopicOptions { Name = "logs.api-", MatchMode = "prefix", Format = "json" };
        Assert.Equal(@"^logs\.api-.*", MqTopicMatcher.KafkaSubscription(rule));
        var actual = MqTopicMatcher.ForTopic(rule, "logs.api-prod");
        Assert.Equal("logs.api-prod", actual.Name);
        Assert.Equal("logs.api-", rule.Name);
        Assert.Equal("json", actual.Format);
    }
}
