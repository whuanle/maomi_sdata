using Mediator;
using Microsoft.EntityFrameworkCore;
using SData.Database;
using SData.Infra.Exceptions;
using SData.Infra.Models;
using SData.Infra.Services;
using SData.Rum.Models;

namespace SData.Rum.Handlers;

/// <summary>
/// 保存 RUM 项目处理（仅 admin 可写，Controller 角色控制）.
/// </summary>
public class SaveRumProjectCommandHandler : IRequestHandler<SaveRumProjectCommand, Simple<long>>
{
    private readonly DatabaseContext _databaseContext;
    private readonly IIdProvider _idProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaveRumProjectCommandHandler"/> class.
    /// </summary>
    /// <param name="databaseContext">SQLite 元数据库.</param>
    /// <param name="idProvider">雪花 id.</param>
    public SaveRumProjectCommandHandler(DatabaseContext databaseContext, IIdProvider idProvider)
    {
        _databaseContext = databaseContext;
        _idProvider = idProvider;
    }

    /// <inheritdoc/>
    public async ValueTask<Simple<long>> Handle(SaveRumProjectCommand request, CancellationToken cancellationToken)
    {
        if (!RumOrigins.TryParse(request.DomainsJson, out var origins))
        {
            throw BusinessException.Throw(400, "允许站点必须是完整的 HTTP/HTTPS Origin 数组");
        }

        request.DomainsJson = System.Text.Json.JsonSerializer.Serialize(origins);
        if (request.Id == 0)
        {
            var exists = await _databaseContext.RumProjects.AnyAsync(x => x.ProjectKey == request.ProjectKey, cancellationToken);
            if (exists)
            {
                throw BusinessException.Throw(409, "RUM 项目标识已存在");
            }

            var project = new Database.Entities.RumProjectEntity
            {
                Id = _idProvider.GetLongId(),
                ProjectKey = request.ProjectKey,
                Name = request.Name,
                DomainsJson = string.IsNullOrEmpty(request.DomainsJson) ? "[]" : request.DomainsJson,
                PerfSampleRate = request.PerfSampleRate,
                ReplaySampleRate = request.ReplaySampleRate,
            };

            _databaseContext.RumProjects.Add(project);
            await _databaseContext.SaveChangesAsync(cancellationToken);
            return new Simple<long> { Value = project.Id };
        }

        var existing = await _databaseContext.RumProjects.FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken)
            ?? throw BusinessException.Throw(404, "RUM 项目不存在");

        // ProjectKey 创建后不可变，只更新其余字段
        existing.Name = request.Name;
        existing.DomainsJson = string.IsNullOrEmpty(request.DomainsJson) ? "[]" : request.DomainsJson;
        existing.PerfSampleRate = request.PerfSampleRate;
        existing.ReplaySampleRate = request.ReplaySampleRate;
        await _databaseContext.SaveChangesAsync(cancellationToken);
        return new Simple<long> { Value = existing.Id };
    }
}

/// <summary>
/// 删除 RUM 项目处理.
/// </summary>
public class DeleteRumProjectCommandHandler : IRequestHandler<DeleteRumProjectCommand, EmptyCommandResponse>
{
    private readonly DatabaseContext _databaseContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeleteRumProjectCommandHandler"/> class.
    /// </summary>
    /// <param name="databaseContext">SQLite 元数据库.</param>
    public DeleteRumProjectCommandHandler(DatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    /// <inheritdoc/>
    public async ValueTask<EmptyCommandResponse> Handle(DeleteRumProjectCommand request, CancellationToken cancellationToken)
    {
        var project = await _databaseContext.RumProjects.FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken)
            ?? throw BusinessException.Throw(404, "RUM 项目不存在");

        _databaseContext.RumProjects.Remove(project);
        await _databaseContext.SaveChangesAsync(cancellationToken);
        return new EmptyCommandResponse();
    }
}

/// <summary>
/// RUM 项目列表处理.
/// </summary>
public class QueryRumProjectsCommandHandler : IRequestHandler<QueryRumProjectsCommand, PagedResult<RumProjectItem>>
{
    private readonly DatabaseContext _databaseContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryRumProjectsCommandHandler"/> class.
    /// </summary>
    /// <param name="databaseContext">SQLite 元数据库.</param>
    public QueryRumProjectsCommandHandler(DatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    /// <inheritdoc/>
    public async ValueTask<PagedResult<RumProjectItem>> Handle(QueryRumProjectsCommand request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var query = _databaseContext.RumProjects.AsNoTracking().OrderByDescending(x => x.Id);

        if (!string.IsNullOrEmpty(request.Search))
        {
            var keyword = $"%{request.Search}%";
            query = query.Where(x =>
                EF.Functions.Like(x.ProjectKey, keyword) ||
                EF.Functions.Like(x.Name, keyword)).OrderByDescending(x => x.Id);
        }

        var count = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new RumProjectItem
            {
                Id = x.Id,
                ProjectKey = x.ProjectKey,
                Name = x.Name,
                DomainsJson = x.DomainsJson,
                PerfSampleRate = x.PerfSampleRate,
                ReplaySampleRate = x.ReplaySampleRate,
                CreateTime = x.CreateTime,
                UpdateTime = x.UpdateTime,
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<RumProjectItem>
        {
            Page = page,
            PageSize = pageSize,
            Count = count,
            Data = items,
        };
    }
}
