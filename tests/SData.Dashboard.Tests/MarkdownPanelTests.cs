using System.Text.Json;
using FluentValidation.TestHelper;
using global::Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Dashboard;
using SData.Infra.Services;

namespace SData.Dashboard.Tests;

/// <summary>静态 Markdown 配置与查询隔离.</summary>
public class MarkdownPanelTests
{
    [Fact]
    public async Task Markdown_RoundTripsAndValidatesWithoutQuery()
    {
        var panel = new PanelConfig { Id = "notes", Type = PanelTypes.Markdown, Markdown = "# Notes\n\n**Healthy**" };
        var json = JsonSerializer.Serialize(new DashboardConfig { Panels = [panel] }, DashboardJson.Options);
        DashboardConfigValidator.Validate(json);
        var parsed = JsonSerializer.Deserialize<DashboardConfig>(json, DashboardJson.Options)!;
        Assert.Equal(panel.Markdown, parsed.Panels[0].Markdown);
        Assert.Null(parsed.Panels[0].Query);
        var result = await new AutoValidator<PreviewPanelCommand>().TestValidateAsync(new PreviewPanelCommand { Panel = panel });
        Assert.True(result.IsValid);

        panel.Markdown = new string('x', PanelTypes.MarkdownMaxLength + 1);
        Assert.ThrowsAny<Exception>(() => DashboardConfigValidator.Validate(JsonSerializer.Serialize(new DashboardConfig { Panels = [panel] }, DashboardJson.Options)));
        var invalid = await new AutoValidator<PreviewPanelCommand>().TestValidateAsync(new PreviewPanelCommand { Panel = panel });
        Assert.False(invalid.IsValid);
    }

    [Fact]
    public async Task Markdown_NeverCallsAnyQueryDependency()
    {
        var executor = new PanelQueryExecutor(null!, null!, null!, null!, null!, NullLogger<PanelQueryExecutor>.Instance);
        foreach (var query in new PanelQueryConfig?[] { null, new() { Kind = PanelQueryKinds.Sql, Sql = "SELECT 1" } })
        {
            var data = await executor.ExecuteAsync(new PanelConfig
            {
                Id = "notes", Type = PanelTypes.Markdown, Markdown = "# Notes",
                Sql = "SELECT 1", QueryId = 123, Query = query,
            }, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow);
            Assert.Null(data.Error);
            Assert.Empty(data.Rows);
            Assert.Empty(data.Series);
            Assert.Equal(0, data.ElapsedMs);
        }
    }
}
