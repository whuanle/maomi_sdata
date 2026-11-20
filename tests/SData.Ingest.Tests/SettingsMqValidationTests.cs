using FluentValidation;
using FluentValidation.TestHelper;
using SData.Infra.Configuration;
using SData.Infra.Services;
using SData.Settings;
using Xunit;

namespace SData.Ingest.Tests;

/// <summary>
/// MQ 实例保存命令校验测试：名称唯一/类型白名单/启用即连接必填/regex 可编译/csv 分隔符单字符/格式白名单.
/// </summary>
public class SettingsMqValidationTests
{
    private readonly IValidator<SaveMqInstancesCommand> _validator = new AutoValidator<SaveMqInstancesCommand>();

    /// <summary>
    /// 合法实例列表（Kafka/RabbitMQ/Pulsar 各一）通过校验.
    /// </summary>
    [Fact]
    public void Validate_ValidInstances_Passes()
    {
        var command = new SaveMqInstancesCommand
        {
            Instances =
            [
                new MqInstanceOptions
                {
                    Id = 1,
                    Name = "k1",
                    Type = "kafka",
                    Enabled = true,
                    BootstrapServers = "b:9092",
                    GroupId = "g",
                    AutoOffsetReset = "earliest",
                    Topics = [new MqTopicOptions { Name = "t", Format = MqLogFormat.Regex, Parser = new MqParserOptions { Pattern = "(?<level>\\w+) (?<msg>.*)", SeverityField = "level", BodyField = "msg" } }],
                },
                new MqInstanceOptions { Id = 2, Name = "r1", Type = "rabbitmq", Enabled = true, Uri = "amqp://guest:guest@localhost:5672/", Topics = [new MqTopicOptions { Name = "q", Format = MqLogFormat.Raw }] },
                new MqInstanceOptions { Id = 3, Name = "p1", Type = "pulsar", Enabled = false, Topics = [] },
            ],
        };

        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    /// <summary>
    /// 实例名称重复 / 未知类型 / 已启用但缺连接 / regex 不可编译 / csv 分隔符多字符 / 未知格式 均被拒绝.
    /// </summary>
    [Fact]
    public void Validate_InvalidInstances_Fails()
    {
        var command = new SaveMqInstancesCommand
        {
            Instances =
            [
                new MqInstanceOptions { Id = 1, Name = "dup", Type = "kafka", Enabled = false, Topics = [] },
                new MqInstanceOptions { Id = 2, Name = "dup", Type = "redis", Enabled = false, Topics = [] },
                new MqInstanceOptions { Id = 3, Name = "no-conn", Type = "kafka", Enabled = true, BootstrapServers = "", Topics = [] },
                new MqInstanceOptions
                {
                    Id = 4,
                    Name = "bad-regex",
                    Type = "kafka",
                    Enabled = true,
                    BootstrapServers = "b:9092",
                    Topics = [new MqTopicOptions { Name = "t", Format = MqLogFormat.Regex, Parser = new MqParserOptions { Pattern = "(?bad[unclosed" } }],
                },
                new MqInstanceOptions
                {
                    Id = 5,
                    Name = "bad-csv",
                    Type = "kafka",
                    Enabled = true,
                    BootstrapServers = "b:9092",
                    Topics = [new MqTopicOptions { Name = "t", Format = MqLogFormat.Csv, Parser = new MqParserOptions { Delimiter = ",," } }],
                },
                new MqInstanceOptions
                {
                    Id = 6,
                    Name = "bad-format",
                    Type = "kafka",
                    Enabled = true,
                    BootstrapServers = "b:9092",
                    Topics = [new MqTopicOptions { Name = "t", Format = "xml" }],
                },
            ],
        };

        var result = _validator.TestValidate(command);
        Assert.NotEmpty(result.Errors);
    }
}
