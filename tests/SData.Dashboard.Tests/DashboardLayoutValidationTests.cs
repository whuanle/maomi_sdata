using global::Xunit;
using SData.Infra.Exceptions;

namespace SData.Dashboard.Tests;

public class DashboardLayoutValidationTests
{
    [Theory]
    [InlineData("""{"version":2,"panels":[{"id":"p","type":"line","layout":{"x":8,"y":0,"w":6,"h":4}}]}""")]
    [InlineData("""{"version":2,"panels":[{"id":"p","layout":{"w":6,"h":4}}]}""")]
    [InlineData("""{"version":99,"panels":[]}""")]
    [InlineData("""{"panels":[{"id":"p"},{"id":"p"}]}""")]
    [InlineData("""{"version":2,"panels":null}""")]
    public void RejectInvalidLayouts(string json)
    {
        var error = Assert.Throws<BusinessException>(() => DashboardConfigValidator.Validate(json));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void AcceptLegacyAndVersionTwo()
    {
        DashboardConfigValidator.Validate("""{"panels":[{"id":"p","sql":"SELECT 1"}],"screen":{"rotationSeconds":30}}""");
        DashboardConfigValidator.Validate("""{"version":2,"panels":[{"id":"p","layout":{"x":6,"y":4,"w":6,"h":3}}]}""");
    }

    [Fact]
    public void CustomRangeKeepsAbsoluteDates()
    {
        var from = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var to = from.AddHours(2);
        Assert.Equal((from, to), PanelRange.Resolve(new PanelTimeConfig { Preset = "custom", From = from, To = to }, null, null));
    }
    [Theory]
    [InlineData("99", "builder")]
    [InlineData("true", "builder")]
    [InlineData("1", "script")]
    public void RejectInvalidChartEditor(string version, string mode)
    {
        var json = "{\"panels\":[{\"id\":\"p\",\"explorer\":{\"editor\":{\"version\":" + version + ",\"mode\":\"" + mode + "\"}}}]}";
        var error = Assert.Throws<BusinessException>(() => DashboardConfigValidator.Validate(json));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void AcceptMqttChartAndCustomEditors()
    {
        DashboardConfigValidator.Validate("""{"panels":[{"id":"mqtt","editor":{"version":1,"mode":"builder","source":"iot","chartType":"time_series","granularity":"1m","series":[],"formulas":[],"groupBy":["device_key","signal"]}}]}""");
        using var document = System.Text.Json.JsonDocument.Parse("""{"version":1,"mode":"builder","source":"iot","shape":"series","aggregation":"avg","field":"num_value","where":"","groupBy":["device_key"],"granularity":"auto"}""");
        Assert.True(CustomNodeEditorValidator.IsValid(document.RootElement, "sql"));
    }

    [Fact]
    public void AcceptVersionedChartEditor()
    {
        DashboardConfigValidator.Validate("""{"panels":[{"id":"p","explorer":{"editor":{"version":1,"mode":"builder","source":"logs","chartType":"time_series","granularity":"5m","series":[],"formulas":[],"groupBy":[]}}}]}""");
    }

}
