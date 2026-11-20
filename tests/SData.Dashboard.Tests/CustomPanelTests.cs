using System.Text.Json;
using FluentValidation.TestHelper;
using global::Xunit;
using SData.Infra.Exceptions;
using SData.Infra.Services;
using SData.Store;
using SData.Store.Models;

namespace SData.Dashboard.Tests;

public class CustomPanelTests
{
    [Fact]
    public async Task ConfigurationRoundTripsAndSavePreviewUseSameValidation()
    {
        var panel = Panel();
        var json = JsonSerializer.Serialize(new DashboardConfig { Panels = [panel] }, DashboardJson.Options);
        DashboardConfigValidator.Validate(json);
        var restored = JsonSerializer.Deserialize<DashboardConfig>(json, DashboardJson.Options)!.Panels[0];
        Assert.Equal("SELECT $value AS value", restored.Query!.Custom!.Nodes[0].Sql);
        Assert.True((await new AutoValidator<PreviewPanelCommand>().TestValidateAsync(new PreviewPanelCommand { Panel = restored })).IsValid);

        restored.Query.Custom.Nodes[0].Parameters["value"] = JsonSerializer.SerializeToElement(new[] { 1 });
        Assert.False((await new AutoValidator<PreviewPanelCommand>().TestValidateAsync(new PreviewPanelCommand { Panel = restored })).IsValid);
        Assert.Throws<BusinessException>(() => DashboardConfigValidator.Validate(
            JsonSerializer.Serialize(new DashboardConfig { Panels = [restored] }, DashboardJson.Options)));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(0)]
    public void UnsupportedVersionsRejected(int version)
    {
        var panel = Panel();
        panel.Query!.Custom!.Version = version;
        Assert.NotNull(CustomPanelDefinition.Validate(panel.Query.Custom));
    }

    [Fact]
    public void InvalidDependenciesAndOutputShapesRejected()
    {
        var definition = Panel().Query!.Custom!;
        definition.Nodes.Add(new() { Ref = "B", Kind = "expression", Expression = "B + A" });
        Assert.NotNull(CustomPanelDefinition.Validate(definition));
        definition.Nodes[1].Expression = "A";
        definition.Outputs = ["B"];
        Assert.Null(CustomPanelDefinition.Validate(definition, "stat"));
        Assert.NotNull(CustomPanelDefinition.Validate(definition, "line"));
        definition.Nodes[0].Shape = "table";
        Assert.NotNull(CustomPanelDefinition.Validate(definition));
        definition.Nodes = [null!];
        Assert.NotNull(CustomPanelDefinition.Validate(definition));
    }

    [Fact]
    public async Task TruncationDiagnosticsAndCancellationPropagate()
    {
        var analytics = new StubAnalytics(new()
        {
            Columns = ["value"], Rows = [[7]], Truncated = true,
        });
        var executor = new CustomPanelExecutor(analytics);
        var data = new PanelData();
        await executor.ExecuteAsync(Panel().Query!.Custom!, data, 500, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, default);
        Assert.True(data.Truncated);
        Assert.True(data.Nodes[0].Truncated);
        Assert.Equal("A", data.Rows[0][3]);
        Assert.Equal(7d, data.Rows[0][2]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(
            Panel().Query!.Custom!, new(), 500, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, cancellation.Token));
    }

    [Fact]
    public async Task ResultBudgetRejectsWideInputs()
    {
        var analytics = new StubAnalytics(new()
        {
            Columns = Enumerable.Range(0, 201).Select(x => "c" + x).ToList(),
            Rows = [Enumerable.Repeat<object?>(1, 201).ToList()],
        });
        var error = await Assert.ThrowsAsync<ArgumentException>(() => new CustomPanelExecutor(analytics).ExecuteAsync(
            Panel().Query!.Custom!, new(), 500, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, default));
        Assert.Contains("预算", error.Message);
    }

    [Fact]
    public async Task CategoryKeysRemainDistinctAndDiagnosticsAreBounded()
    {
        var definition = Panel().Query!.Custom!;
        definition.Nodes[0].Shape = "category";
        definition.Nodes[0].GroupFields = ["service"];
        var analytics = new StubAnalytics(new()
        {
            Columns = ["service", "value"],
            Rows = Enumerable.Range(0, 40).Select(x => new List<object?> { "service-" + x, x }).ToList(),
        });
        var data = new PanelData();
        await new CustomPanelExecutor(analytics).ExecuteAsync(definition, data, 500, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, default);
        Assert.Equal(40, data.Rows.Count);
        Assert.Equal(40, data.Rows.Select(x => x[3]).Distinct().Count());
        Assert.Equal(30, data.Nodes[0].Rows.Count);
    }

    [Fact]
    public async Task AutomaticBucketsRebindWithoutDependingOnEditor()
    {
        var definition = Panel().Query!.Custom!;
        definition.Nodes[0].AutoGranularity = true;
        definition.Nodes[0].Parameters["chart_bucket"] = "stale";
        definition.Nodes[0].Editor = JsonSerializer.SerializeToElement(new
        {
            version = 1, mode = "builder", source = "logs", shape = "scalar",
            aggregation = "count", field = "", where = "", groupBy = Array.Empty<string>(), granularity = "auto",
        });
        Assert.Null(CustomPanelDefinition.Validate(definition));
        var analytics = new StubAnalytics(new() { Columns = ["value"], Rows = [[7]] });
        var executor = new CustomPanelExecutor(analytics);
        var end = DateTimeOffset.UtcNow;
        await executor.ExecuteAsync(definition, new(), 500, end.AddHours(-1), end, default);
        Assert.Equal("1 minute", analytics.LastParameters!["chart_bucket"]);
        definition.Nodes[0].Editor = null;
        await executor.ExecuteAsync(definition, new(), 500, end.AddDays(-10), end, default);
        Assert.Equal("1 day", analytics.LastParameters!["chart_bucket"]);
        Assert.Equal("stale", definition.Nodes[0].Parameters["chart_bucket"]);
    }

    [Theory]
    [InlineData("{\"version\":\"1\"}")]
    [InlineData("{\"version\":99}")]
    [InlineData("true")]
    public void InvalidNodeEditorRejected(string json)
    {
        var definition = Panel().Query!.Custom!;
        definition.Nodes[0].Editor = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.NotNull(CustomPanelDefinition.Validate(definition));
    }

    private static PanelConfig Panel() => new()
    {
        Id = "custom", Type = "stat", Title = "自定义组件",
        Query = new()
        {
            Kind = PanelQueryKinds.Custom,
            Custom = new()
            {
                Nodes = [new() { Ref = "A", Sql = "SELECT $value AS value", Parameters = new() { ["value"] = 7 } }],
                Outputs = ["A"],
            },
        },
    };

    private sealed class StubAnalytics(AnalyticsQueryResult result) : IAnalyticsExecutor
    {
        public IReadOnlyDictionary<string, object?>? LastParameters { get; private set; }
        public Task<AnalyticsQueryResult> ExecuteAsync(string sql, int rowLimit = 1000, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("自定义查询必须经时间窗分析通道");

        public Task<AnalyticsQueryResult> ExecuteSearchAsync(string sql, DateTimeOffset from, DateTimeOffset to, int rowLimit = 500,
            IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastParameters = parameters;
            return Task.FromResult(result);
        }
    }
}
