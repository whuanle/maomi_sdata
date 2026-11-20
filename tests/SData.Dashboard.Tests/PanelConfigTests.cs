using System.Text.Json;
using FluentValidation.TestHelper;
using global::Xunit;
using SData.Dashboard;
using SData.Infra.Services;
using SData.Store.QueryDsl;

namespace SData.Dashboard.Tests;

/// <summary>
/// 面板配置协议测试：configJson 反序列化（新/旧协议）与 PreviewPanelCommand 校验规则.
/// </summary>
public class PanelConfigTests
{
    /// <summary>
    /// 旧协议 configJson（无 version，面板根挂 sql）兼容解析.
    /// </summary>
    [Fact]
    public void Parse_LegacyConfig()
    {
        const string json = """{"panels":[{"id":"p1","title":"旧面板","type":"line","sql":"SELECT 1","rowLimit":50}]}""";
        var config = JsonSerializer.Deserialize<DashboardConfig>(json, DashboardJson.Options);

        Assert.NotNull(config);
        Assert.Single(config!.Panels);
        var panel = config.Panels[0];
        Assert.Equal("p1", panel.Id);
        Assert.Null(panel.Query);
        Assert.Equal("SELECT 1", panel.Sql);
        Assert.Equal(50, panel.RowLimit);
    }

    /// <summary>
    /// 新协议 configJson（version=1 + query 多形态）往返解析.
    /// </summary>
    [Fact]
    public void Parse_VersionedConfig_RoundTrip()
    {
        var config = new DashboardConfig
        {
            Version = 1,
            Time = new PanelTimeConfig { Preset = "6h" },
            RefreshSeconds = 30,
            Panels =
            [
                new PanelConfig
                {
                    Id = "p1",
                    Title = "指标",
                    Type = PanelTypes.Line,
                    Layout = new PanelLayoutConfig { W = 6, H = 3 },
                    Unit = "ms",
                    ShowLegend = false,
                    Query = new PanelQueryConfig
                    {
                        Kind = PanelQueryKinds.Metric,
                        Name = "http_duration",
                        Labels = new Dictionary<string, string> { ["service"] = "api" },
                        Aggregation = "p95",
                        StepSeconds = 30,
                        MaxSeries = 10,
                    },
                },
                new PanelConfig
                {
                    Id = "p2",
                    Title = "错误日志",
                    Type = PanelTypes.Bar,
                    Query = new PanelQueryConfig
                    {
                        Kind = PanelQueryKinds.Dsl,
                        Domain = "logs",
                        Dsl = new QueryDslModel
                        {
                            Bucket = new QueryBucket { Step = "1m", Func = "count" },
                            GroupBy = ["service"],
                        },
                    },
                },
            ],
        };

        var json = JsonSerializer.Serialize(config, DashboardJson.Options);
        var parsed = JsonSerializer.Deserialize<DashboardConfig>(json, DashboardJson.Options);

        Assert.NotNull(parsed);
        Assert.Equal("6h", parsed!.Time?.Preset);
        Assert.Equal(30, parsed.RefreshSeconds);
        Assert.Equal(2, parsed.Panels.Count);
        Assert.Equal(6, parsed.Panels[0].Layout.W);
        Assert.Equal("ms", parsed.Panels[0].Unit);
        Assert.False(parsed.Panels[0].ShowLegend);
        Assert.Equal(PanelQueryKinds.Metric, parsed.Panels[0].Query!.Kind);
        Assert.Equal("api", parsed.Panels[0].Query.Labels["service"]);
        Assert.Equal(PanelQueryKinds.Dsl, parsed.Panels[1].Query!.Kind);
        Assert.Equal("1m", parsed.Panels[1].Query.Dsl!.Bucket!.Step);
    }

    /// <summary>
    /// 校验：合法 metric/dsl/promql/sql 面板通过.
    /// </summary>
    [Fact]
    public async Task Validate_ValidKinds_Pass()
    {
        var validator = new AutoValidator<PreviewPanelCommand>();
        var from = DateTimeOffset.UtcNow;
        var to = from.AddHours(1);
        var cases = new[]
        {
            new PanelQueryConfig { Kind = PanelQueryKinds.Metric, Name = "m", Aggregation = "avg", StepSeconds = 60, MaxSeries = 20 },
            new PanelQueryConfig { Kind = PanelQueryKinds.Dsl, Domain = "logs", Dsl = new QueryDslModel { Bucket = new QueryBucket { Step = "1m", Func = "count" } } },
            new PanelQueryConfig { Kind = PanelQueryKinds.Promql, Expr = "rate(x[5m])", StepSeconds = 60, MaxSeries = 20 },
            new PanelQueryConfig { Kind = PanelQueryKinds.Sql, Sql = "SELECT 1" },
        };
        foreach (var query in cases)
        {
            var command = new PreviewPanelCommand { Panel = new PanelConfig { Id = "p1", Query = query }, From = from, To = to };
            var result = await validator.TestValidateAsync(command);
            Assert.True(result.IsValid, $"{query.Kind} 应通过校验：{string.Join(';', result.Errors.Select(x => x.ErrorMessage))}");
        }
    }

    /// <summary>
    /// 校验：非法 kind / metric 缺名 / dsl 无 bucket 无聚合 / from&gt;to / 类型越界 均拒绝.
    /// </summary>
    [Fact]
    public async Task Validate_InvalidCases_Fail()
    {
        var validator = new AutoValidator<PreviewPanelCommand>();
        var from = DateTimeOffset.UtcNow;
        var to = from.AddHours(1);
        var cases = new[]
        {
            new PanelQueryConfig { Kind = "nope" },
            new PanelQueryConfig { Kind = PanelQueryKinds.Metric, Aggregation = "avg" },
            new PanelQueryConfig { Kind = PanelQueryKinds.Metric, Name = "m", Aggregation = "median" },
            new PanelQueryConfig { Kind = PanelQueryKinds.Metric, Name = "m", StepSeconds = 5 },
            new PanelQueryConfig { Kind = PanelQueryKinds.Dsl, Domain = "logs" },
            new PanelQueryConfig { Kind = PanelQueryKinds.Dsl, Domain = "redis", Dsl = new QueryDslModel { Bucket = new QueryBucket() } },
            new PanelQueryConfig { Kind = PanelQueryKinds.Promql },
            new PanelQueryConfig { Kind = PanelQueryKinds.Sql },
        };
        foreach (var query in cases)
        {
            var command = new PreviewPanelCommand { Panel = new PanelConfig { Id = "p1", Query = query }, From = from, To = to };
            var result = await validator.TestValidateAsync(command);
            Assert.False(result.IsValid, $"{query.Kind} 应被拒绝");
        }

        var badRange = new PreviewPanelCommand { Panel = new PanelConfig { Id = "p1", Query = new PanelQueryConfig { Kind = PanelQueryKinds.Sql, Sql = "SELECT 1" } }, From = to, To = from };
        var rangeResult = await validator.TestValidateAsync(badRange);
        Assert.False(rangeResult.IsValid);
    }

    /// <summary>
    /// PanelRange：请求覆盖 &gt; 配置 preset &gt; 缺省 1h；from&gt;to 抛 400.
    /// </summary>
    [Fact]
    public void Resolve_Range_Precedence()
    {
        var now = DateTimeOffset.UtcNow;
        var (from, to) = PanelRange.Resolve(null, null, null);
        Assert.True(to - from <= TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(5)));

        var (from2, to2) = PanelRange.Resolve(new PanelTimeConfig { Preset = "7d" }, null, now);
        Assert.Equal(TimeSpan.FromDays(7), to2 - from2);

        var (from3, to3) = PanelRange.Resolve(new PanelTimeConfig { Preset = "7d" }, now.AddDays(-1), now);
        Assert.Equal(TimeSpan.FromDays(1), to3 - from3);

        Assert.Throws<SData.Infra.Exceptions.BusinessException>(() => PanelRange.Resolve(null, now, now));
    }
}
