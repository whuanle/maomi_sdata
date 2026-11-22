using System.Text.RegularExpressions;
using SData.Infra.Configuration;

namespace SData.Ingest.Consumers;

/// <summary>
/// 消费规则匹配：精确优先，其次最长前缀；比较区分大小写.
/// </summary>
public static class MqTopicMatcher
{
    /// <summary>选择实际主题命中的唯一规则.</summary>
    public static MqTopicOptions? Match(IEnumerable<MqTopicOptions> rules, string name)
    {
        return rules.Where(rule => rule.MatchMode == "exact"
                ? string.Equals(rule.Name, name, StringComparison.Ordinal)
                : rule.MatchMode == "prefix" && name.StartsWith(rule.Name, StringComparison.Ordinal))
            .OrderByDescending(rule => rule.MatchMode == "exact")
            .ThenByDescending(rule => rule.Name.Length)
            .FirstOrDefault();
    }

    /// <summary>Kafka 的 librdkafka 订阅表达式（仅前缀使用正则）.</summary>
    public static string KafkaSubscription(MqTopicOptions rule)
    {
        return rule.MatchMode == "prefix" ? "^" + Regex.Escape(rule.Name) + ".*" : rule.Name;
    }

    /// <summary>复制规则并记录真实来源，避免修改配置快照.</summary>
    public static MqTopicOptions ForTopic(MqTopicOptions rule, string name)
    {
        return new MqTopicOptions
        {
            Name = name,
            MatchMode = rule.MatchMode,
            Format = rule.Format,
            Service = rule.Service,
            Severity = rule.Severity,
            Parser = rule.Parser,
        };
    }
}
