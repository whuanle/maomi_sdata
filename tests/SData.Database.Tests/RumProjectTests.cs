using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Database.Sqlite;
using SData.Infra.Exceptions;
using SData.Infra.Models;
using SData.Infra.Services;
using SData.Rum.Handlers;
using SData.Rum.Models;

namespace SData.Database.Tests;

/// <summary>
/// RUM 项目 CRUD 与角色语义测试（T611）.
/// </summary>
public class RumProjectTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDatabaseContext _dbContext;
    private readonly SaveRumProjectCommandHandler _saveHandler;
    private readonly QueryRumProjectsCommandHandler _queryHandler;
    private readonly DeleteRumProjectCommandHandler _deleteHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="RumProjectTests"/> class.
    /// </summary>
    public RumProjectTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sdata-rumproj-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _dbContext = new SqliteDatabaseContext(options);
        _dbContext.Database.EnsureCreated();
        SchemaReconciler.Apply(_dbContext, NullLogger.Instance);

        var idProvider = new DefaultIdProvider(1);
        _saveHandler = new SaveRumProjectCommandHandler(_dbContext, idProvider);
        _queryHandler = new QueryRumProjectsCommandHandler(_dbContext);
        _deleteHandler = new DeleteRumProjectCommandHandler(_dbContext);
    }

    private async Task<Simple<long>> SaveAsync(string projectKey = "rum_demo", string name = "演示项目")
    {
        return await _saveHandler.Handle(new SaveRumProjectCommand
        {
            ProjectKey = projectKey,
            Name = name,
            DomainsJson = "[\"https://intranet.local\"]",
            PerfSampleRate = 0.5,
            ReplaySampleRate = 0.2,
            ContextUserId = 1,
            ContextUserRole = SData.Infra.SDataRoles.Admin,
        }, CancellationToken.None);
    }

    /// <summary>
    /// CRUD 全链：新增 → 查询 → 修改（Key 不可变）→ 删除.
    /// </summary>
    [Fact]
    public async Task Crud_SaveQueryUpdateDelete()
    {
        var saved = await SaveAsync();
        Assert.True(saved.Value > 0);

        var paged = await _queryHandler.Handle(new QueryRumProjectsCommand { Page = 1, PageSize = 20 }, CancellationToken.None);
        Assert.Equal(1, paged.Count);
        var item = Assert.Single(paged.Data);
        Assert.Equal("rum_demo", item.ProjectKey);
        Assert.Equal(0.5, item.PerfSampleRate);
        Assert.Equal("[\"https://intranet.local\"]", item.DomainsJson);

        // 修改：Key 不可变，采样率可调
        await _saveHandler.Handle(new SaveRumProjectCommand
        {
            Id = saved.Value,
            ProjectKey = "rum_hacked",
            Name = "改名项目",
            PerfSampleRate = 0.9,
            ReplaySampleRate = 0.1,
        }, CancellationToken.None);

        var updated = await _queryHandler.Handle(new QueryRumProjectsCommand { Page = 1, PageSize = 20 }, CancellationToken.None);
        var updatedItem = Assert.Single(updated.Data);
        Assert.Equal("rum_demo", updatedItem.ProjectKey); // Key 不变
        Assert.Equal("改名项目", updatedItem.Name);
        Assert.Equal(0.9, updatedItem.PerfSampleRate);

        await _deleteHandler.Handle(new DeleteRumProjectCommand { Id = saved.Value }, CancellationToken.None);
        var afterDelete = await _queryHandler.Handle(new QueryRumProjectsCommand { Page = 1, PageSize = 20 }, CancellationToken.None);
        Assert.Equal(0, afterDelete.Count);

        // 底层软删除标记
        Assert.Equal(0, await _dbContext.RumProjects.IgnoreQueryFilters().CountAsync(x => x.ProjectKey == "rum_demo" && x.IsDeleted == 0));
    }

    /// <summary>
    /// 重复 ProjectKey 新增返回 409.
    /// </summary>
    [Fact]
    public async Task Save_DuplicateKey_ThrowsConflict()
    {
        await SaveAsync();

        var exception = await Assert.ThrowsAsync<BusinessException>(() => SaveAsync());
        Assert.Equal(409, exception.StatusCode);
    }

    /// <summary>
    /// 不存在的项目修改/删除返回 404.
    /// </summary>
    [Fact]
    public async Task UpdateAndDelete_Missing_ThrowsNotFound()
    {
        var updateException = await Assert.ThrowsAsync<BusinessException>(async () => await _saveHandler.Handle(new SaveRumProjectCommand
        {
            Id = 99999,
            ProjectKey = "rum_demo",
            Name = "x",
        }, CancellationToken.None));
        Assert.Equal(404, updateException.StatusCode);

        var deleteException = await Assert.ThrowsAsync<BusinessException>(async () => await _deleteHandler.Handle(new DeleteRumProjectCommand
        {
            Id = 99999,
        }, CancellationToken.None));
        Assert.Equal(404, deleteException.StatusCode);
    }

    /// <summary>
    /// 403 语义：写接口仅 admin 角色（readonly 用户不在角色集，HTTP 层将被 403 拒绝）.
    /// </summary>
    [Fact]
    public void Controller_WriteActions_RequireAdminRole()
    {
        var controllerType = typeof(SData.Rum.Api.Controllers.RumProjectsController);
        foreach (var method in new[] { nameof(SData.Rum.Api.Controllers.RumProjectsController.Save), nameof(SData.Rum.Api.Controllers.RumProjectsController.Delete) })
        {
            var attribute = controllerType.GetMethod(method)!.GetCustomAttributes(false)
                .OfType<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .SingleOrDefault(a => !string.IsNullOrEmpty(a.Roles));
            Assert.NotNull(attribute);
            Assert.Equal(SData.Infra.SDataRoles.Admin, attribute!.Roles);
            Assert.DoesNotContain(SData.Infra.SDataRoles.Write, attribute.Roles);
            Assert.DoesNotContain("readonly", attribute.Roles);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _dbContext.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }
}
