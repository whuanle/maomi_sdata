using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using global::Xunit;
using SData.Infra.Exceptions;

namespace SData.Dashboard.Tests;

public class DashboardGroupTests
{
    [Fact]
    public void RoundTripGroupAndLegacyMembers()
    {
        const string json = """{"version":3,"panels":[{"id":"g","type":"group","layout":{"x":0,"y":0,"w":12,"h":4},"defaultPanelId":"b","panels":[{"id":"a","sql":"SELECT 1"},{"id":"b","type":"markdown","markdown":"说明"}]}]}""";
        DashboardConfigValidator.Validate(json);
        var config = JsonSerializer.Deserialize<DashboardConfig>(json, DashboardJson.Options)!;
        Assert.Equal("b", config.Panels[0].DefaultPanelId);
        Assert.Equal("SELECT 1", config.Panels[0].Panels![0].Sql);
        DashboardConfigValidator.Validate(JsonSerializer.Serialize(config, DashboardJson.Options));
    }

    [Fact]
    public async Task DataEndpointExecutesOnlyMembersAndContainerNeverQueries()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new SData.Database.DatabaseContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<SData.Database.DatabaseContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Dashboards.Add(new SData.Database.Entities.DashboardEntity
        {
            Id = 1, Name = "group", Kind = "view",
            ConfigJson = """{"version":3,"panels":[{"id":"g","type":"group","panels":[{"id":"a","type":"markdown"},{"id":"b","type":"markdown"}]}]}""",
        });
        await db.SaveChangesAsync();
        var executor = new PanelQueryExecutor(null!, null!, null!, null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PanelQueryExecutor>.Instance);
        var result = await new SData.Dashboard.Handlers.GetDashboardDataCommandHandler(db, executor,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SData.Dashboard.Handlers.GetDashboardDataCommandHandler>.Instance)
            .Handle(new() { Id = 1 }, default);
        Assert.Equal(new[] { "a", "b" }, result.Panels.Select(panel => panel.Id));
        var container = await executor.ExecuteAsync(new() { Id = "g", Type = PanelTypes.Group, Sql = "INVALID SQL" },
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow);
        Assert.Null(container.Error);
        Assert.Empty(container.Rows);
    }

    [Theory]
    [InlineData("""{"version":2,"panels":[{"id":"g","type":"group","layout":{"x":0,"y":0,"w":6,"h":4},"panels":[{"id":"a"}]}]}""")]
    [InlineData("""{"version":3,"panels":[{"id":"g","type":"group","layout":{"x":0,"y":0,"w":6,"h":4},"panels":null}]}""")]
    [InlineData("""{"version":3,"panels":[{"id":"g","type":"group","layout":{"x":0,"y":0,"w":6,"h":4},"panels":[{"id":"a"},{"id":"a"}]}]}""")]
    [InlineData("""{"version":3,"panels":[{"id":"g","type":"group","layout":{"x":0,"y":0,"w":6,"h":4},"defaultPanelId":"missing","panels":[{"id":"a"}]}]}""")]
    [InlineData("""{"version":3,"panels":[{"id":"g","type":"group","layout":{"x":0,"y":0,"w":6,"h":4},"panels":[{"id":"nested","type":"group","panels":[{"id":"a"}]}]}]}""")]
    [InlineData("""{"version":3,"panels":[{"id":"g","type":"group","layout":{"x":0,"y":0,"w":6,"h":4},"sql":"SELECT 1","panels":[{"id":"a"}]}]}""")]
    public void RejectInvalidGroups(string json)
    {
        Assert.Equal(400, Assert.Throws<BusinessException>(() => DashboardConfigValidator.Validate(json)).StatusCode);
    }

    [Fact]
    public void EmptyGroupRoundTripsWithoutDefaultTab()
    {
        const string json = """{"version":3,"panels":[{"id":"g","type":"group","layout":{"x":0,"y":0,"w":6,"h":4},"panels":[]}]}""";
        DashboardConfigValidator.Validate(json);
        var config = JsonSerializer.Deserialize<DashboardConfig>(json, DashboardJson.Options)!;
        Assert.Empty(config.Panels[0].Panels!);
        DashboardConfigValidator.Validate(JsonSerializer.Serialize(config, DashboardJson.Options));
    }

    [Fact]
    public void CountIncludesContainerAndMembers()
    {
        var config = new DashboardConfig { Version = 3, Panels = [
            new PanelConfig { Id = "g", Type = PanelTypes.Group, Layout = new() { X = 0, Y = 0 },
                Panels = Enumerable.Range(0, 100).Select(i => new PanelConfig { Id = i.ToString() }).ToList() }
        ] };
        Assert.Throws<BusinessException>(() => DashboardConfigValidator.Validate(JsonSerializer.Serialize(config, DashboardJson.Options)));
    }
}
