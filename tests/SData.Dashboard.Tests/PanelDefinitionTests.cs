using System.Text.Json;
using FluentValidation.TestHelper;
using global::Xunit;
using SData.Infra.Exceptions;
using SData.Infra.Services;

namespace SData.Dashboard.Tests;

public class PanelDefinitionTests
{
    private const string Visual = """{"version":1,"kind":"number","display":"line","outputs":[{"id":"a","alias":"Requests"}],"range":{"from":"2026-11-18T00:00:00Z","to":"2026-11-18T01:00:00Z"}}""";
    private const string Editor = """{"version":1,"mode":"builder","source":"logs","chartType":"number","granularity":"auto","series":[],"formulas":[],"groupBy":[]}""";

    [Fact]
    public async Task IndependentDefinitionsRoundTripAndPreview()
    {
        var panel = new PanelConfig
        {
            Id = "p", Type = PanelTypes.Stat,
            Visualization = JsonSerializer.Deserialize<JsonElement>(Visual),
            Editor = JsonSerializer.Deserialize<JsonElement>(Editor),
            Query = new PanelQueryConfig
            {
                Kind = PanelQueryKinds.Sql, Sql = "SELECT 1",
                NumberTrend = new PanelTrendQuery { Sql = "SELECT $value", Parameters = new() { ["value"] = 1 } },
            },
        };
        var json = JsonSerializer.Serialize(new DashboardConfig { Panels = [panel] }, DashboardJson.Options);
        DashboardConfigValidator.Validate(json);
        var parsed = JsonSerializer.Deserialize<DashboardConfig>(json, DashboardJson.Options)!.Panels[0];
        Assert.Equal("number", parsed.Visualization!.Value.GetProperty("kind").GetString());
        Assert.Equal("builder", parsed.Editor!.Value.GetProperty("mode").GetString());
        Assert.Equal("SELECT $value", parsed.Query!.NumberTrend!.Sql);
        Assert.Null(parsed.Explorer);
        var result = await new AutoValidator<PreviewPanelCommand>().TestValidateAsync(new PreviewPanelCommand { Panel = parsed });
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("visualization", """{"version":99}""")]
    [InlineData("visualization", """{"version":"1"}""")]
    [InlineData("visualization", """true""")]
    [InlineData("editor", """{"version":99}""")]
    [InlineData("editor", """{"version":1,"mode":"script"}""")]
    public async Task InvalidDefinitionsRejectedBySaveAndPreview(string field, string definition)
    {
        var json = "{\"panels\":[{\"id\":\"p\",\"query\":{\"kind\":\"sql\",\"sql\":\"SELECT 1\"},\"" + field + "\":" + definition + "}]}";
        Assert.Equal(400, Assert.Throws<BusinessException>(() => DashboardConfigValidator.Validate(json)).StatusCode);
        var panel = JsonSerializer.Deserialize<DashboardConfig>(json, DashboardJson.Options)!.Panels[0];
        var result = await new AutoValidator<PreviewPanelCommand>().TestValidateAsync(new PreviewPanelCommand { Panel = panel });
        Assert.False(result.IsValid);
    }

    [Fact]
    public void InvalidTrendRejected()
    {
        var panel = new PanelConfig { Query = new PanelQueryConfig { Kind = PanelQueryKinds.Sql, Sql = "SELECT 1", NumberTrend = new() { Sql = "" } } };
        Assert.NotNull(DashboardConfigValidator.PanelDefinitionError(panel));
        panel.Query.NumberTrend.Sql = "SELECT 1";
        panel.Query.Kind = PanelQueryKinds.Metric;
        Assert.NotNull(DashboardConfigValidator.PanelDefinitionError(panel));
    }
}
