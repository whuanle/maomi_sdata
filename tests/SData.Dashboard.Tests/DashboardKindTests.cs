using FluentValidation.TestHelper;
using global::Xunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Dashboard.Handlers;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Exceptions;
using SData.Infra.Services;

namespace SData.Dashboard.Tests;

/// <summary>退役大屏与普通仪表盘隔离.</summary>
public class DashboardKindTests
{
    [Theory]
    [InlineData("screen")]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task RejectInactiveKinds(string kind)
    {
        var save = await new AutoValidator<SaveDashboardCommand>().TestValidateAsync(new SaveDashboardCommand { Name = "test", Kind = kind });
        save.ShouldHaveValidationErrorFor(x => x.Kind);
        var query = await new AutoValidator<QueryDashboardsCommand>().TestValidateAsync(new QueryDashboardsCommand { Kind = kind });
        query.ShouldHaveValidationErrorFor(x => x.Kind);
    }

    [Fact]
    public async Task ListOnlyViewsBeforeCountingAndPaging()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Dashboards.AddRange(
            new DashboardEntity { Id = 1, Name = "first", Kind = "view" },
            new DashboardEntity { Id = 2, Name = "old screen", Kind = "screen" },
            new DashboardEntity { Id = 3, Name = "last", Kind = "view" });
        await db.SaveChangesAsync();
        var handler = new QueryDashboardsCommandHandler(db);
        foreach (var kind in new string?[] { null, "view" })
        {
            var result = await handler.Handle(new() { Kind = kind, Page = 2, PageSize = 1 }, default);
            Assert.Equal(2, result.Count);
            Assert.Equal(1, Assert.Single(result.Data).Id);
        }
    }

    [Fact]
    public async Task HistoricalScreenIsInaccessibleAndUnchanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        const string config = """{"panels":[],"screen":{"rotationSeconds":30}}""";
        db.Dashboards.Add(new DashboardEntity { Id = 7, Name = "historical", Kind = "screen", ConfigJson = config });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var executor = new PanelQueryExecutor(null!, null!, null!, null!, null!, NullLogger<PanelQueryExecutor>.Instance);
        var operations = new Func<Task>[]
        {
            async () => { await new GetDashboardCommandHandler(db).Handle(new() { Id = 7 }, default); },
            async () => { await new GetDashboardDataCommandHandler(db, executor, NullLogger<GetDashboardDataCommandHandler>.Instance).Handle(new() { Id = 7 }, default); },
            async () => { await new SaveDashboardCommandHandler(db, null!).Handle(new() { Id = 7, Name = "converted", Kind = "view" }, default); },
            async () => { await new DeleteDashboardCommandHandler(db).Handle(new() { Id = 7 }, default); },
        };
        foreach (var operation in operations)
        {
            var error = await Assert.ThrowsAsync<BusinessException>(operation);
            Assert.Equal(404, error.StatusCode);
        }

        var preserved = await db.Dashboards.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal("screen", preserved.Kind);
        Assert.Equal("historical", preserved.Name);
        Assert.Equal(config, preserved.ConfigJson);
        Assert.Equal(0, preserved.IsDeleted);
    }

    [Fact]
    public async Task ViewCrudStillWorks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var save = new SaveDashboardCommandHandler(db, new TestIdProvider());
        var created = await save.Handle(new() { Name = "ordinary" }, default);
        await save.Handle(new() { Id = created.Value, Name = "updated" }, default);
        var item = await new GetDashboardCommandHandler(db).Handle(new() { Id = created.Value }, default);
        Assert.Equal("updated", item.Name);
        Assert.Equal("view", item.Kind);
        var executor = new PanelQueryExecutor(null!, null!, null!, null!, null!, NullLogger<PanelQueryExecutor>.Instance);
        var data = await new GetDashboardDataCommandHandler(db, executor, NullLogger<GetDashboardDataCommandHandler>.Instance)
            .Handle(new() { Id = created.Value }, default);
        Assert.Empty(data.Panels);
        await new DeleteDashboardCommandHandler(db).Handle(new() { Id = created.Value }, default);
        Assert.Empty(await db.Dashboards.ToListAsync());
    }

    private sealed class TestIdProvider : IIdProvider
    {
        public long GetLongId() => 100;
    }
}
