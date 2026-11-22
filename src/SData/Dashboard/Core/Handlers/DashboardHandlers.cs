using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SData.Database;
using SData.Infra.Exceptions;
using SData.Infra.Models;
using SData.Infra.Services;

namespace SData.Dashboard.Handlers;

/// <summary>
/// 仪表盘 CRUD 与数据服务处理.
/// </summary>
public class SaveDashboardCommandHandler : IRequestHandler<SaveDashboardCommand, Simple<long>>
{
    private readonly DatabaseContext _databaseContext;
    private readonly IIdProvider _idProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaveDashboardCommandHandler"/> class.
    /// </summary>
    public SaveDashboardCommandHandler(DatabaseContext databaseContext, IIdProvider idProvider)
    {
        _databaseContext = databaseContext;
        _idProvider = idProvider;
    }

    /// <inheritdoc/>
    public async Task<Simple<long>> Handle(SaveDashboardCommand request, CancellationToken cancellationToken)
    {
        DashboardConfigValidator.Validate(request.ConfigJson);

        if (request.Id == 0)
        {
            var dashboard = new Database.Entities.DashboardEntity
            {
                Id = _idProvider.GetLongId(),
                Name = request.Name,
                Kind = request.Kind,
                Description = request.Description,
                ConfigJson = string.IsNullOrEmpty(request.ConfigJson) ? "{}" : request.ConfigJson,
            };

            _databaseContext.Dashboards.Add(dashboard);
            await _databaseContext.SaveChangesAsync(cancellationToken);
            return new Simple<long> { Value = dashboard.Id };
        }

        await using var transaction = await _databaseContext.Database.BeginTransactionAsync(cancellationToken);
        var existing = await _databaseContext.Dashboards.FirstOrDefaultAsync(x => x.Id == request.Id && x.Kind == "view", cancellationToken)
            ?? throw BusinessException.Throw(404, "仪表盘不存在");

        if (request.ExpectedConfigJson != null && existing.ConfigJson != request.ExpectedConfigJson)
        {
            throw BusinessException.Throw(409, "仪表盘已被修改，请重新加载后保存");
        }

        existing.Name = request.Name;
        existing.Kind = request.Kind;
        existing.Description = request.Description;
        existing.ConfigJson = string.IsNullOrEmpty(request.ConfigJson) ? "{}" : request.ConfigJson;
        await _databaseContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Simple<long> { Value = existing.Id };
    }
}

/// <summary>
/// 删除仪表盘处理.
/// </summary>
public class DeleteDashboardCommandHandler : IRequestHandler<DeleteDashboardCommand, EmptyCommandResponse>
{
    private readonly DatabaseContext _databaseContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeleteDashboardCommandHandler"/> class.
    /// </summary>
    public DeleteDashboardCommandHandler(DatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    /// <inheritdoc/>
    public async Task<EmptyCommandResponse> Handle(DeleteDashboardCommand request, CancellationToken cancellationToken)
    {
        var dashboard = await _databaseContext.Dashboards.FirstOrDefaultAsync(x => x.Id == request.Id && x.Kind == "view", cancellationToken)
            ?? throw BusinessException.Throw(404, "仪表盘不存在");

        _databaseContext.Dashboards.Remove(dashboard);
        await _databaseContext.SaveChangesAsync(cancellationToken);
        return new EmptyCommandResponse();
    }
}

/// <summary>
/// 仪表盘列表处理.
/// </summary>
public class QueryDashboardsCommandHandler : IRequestHandler<QueryDashboardsCommand, PagedResult<DashboardItem>>
{
    private readonly DatabaseContext _databaseContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryDashboardsCommandHandler"/> class.
    /// </summary>
    public QueryDashboardsCommandHandler(DatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    /// <inheritdoc/>
    public async Task<PagedResult<DashboardItem>> Handle(QueryDashboardsCommand request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var query = _databaseContext.Dashboards.AsNoTracking()
            .Where(x => x.Kind == "view")
            .OrderByDescending(x => x.Id);

        var count = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new DashboardItem
            {
                Id = x.Id,
                Name = x.Name,
                Kind = x.Kind,
                Description = x.Description,
                ConfigJson = x.ConfigJson,
                CreateTime = x.CreateTime,
                UpdateTime = x.UpdateTime,
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<DashboardItem>
        {
            Page = page,
            PageSize = pageSize,
            Count = count,
            Data = items,
        };
    }
}

/// <summary>
/// 仪表盘详情处理.
/// </summary>
public class GetDashboardCommandHandler : IRequestHandler<GetDashboardCommand, DashboardItem>
{
    private readonly DatabaseContext _databaseContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="GetDashboardCommandHandler"/> class.
    /// </summary>
    public GetDashboardCommandHandler(DatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    /// <inheritdoc/>
    public async Task<DashboardItem> Handle(GetDashboardCommand request, CancellationToken cancellationToken)
    {
        return await _databaseContext.Dashboards.AsNoTracking()
            .Where(x => x.Id == request.Id && x.Kind == "view")
            .Select(x => new DashboardItem
            {
                Id = x.Id,
                Name = x.Name,
                Kind = x.Kind,
                Description = x.Description,
                ConfigJson = x.ConfigJson,
                CreateTime = x.CreateTime,
                UpdateTime = x.UpdateTime,
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw BusinessException.Throw(404, "仪表盘不存在");
    }
}

/// <summary>
/// 仪表盘数据处理：解析面板配置并按 query.kind 分派执行（旧协议面板兼容回退）.
/// 面板配置约定（version=1）：{"version":1,"time":{"preset":"1h"},"refreshSeconds":0,"panels":[
///   {"id":"p1","title":"标题","type":"line","layout":{"w":6,"h":4},"unit":"short","showLegend":true,
///    "query":{"kind":"metric|dsl|promql|sql", ...}}]}
/// 旧协议（无 query 字段）：{"panels":[{"id":"p1","title":"标题","type":"line","sql":"SELECT ...","queryId":123,"rowLimit":100}]}
/// </summary>
public class GetDashboardDataCommandHandler : IRequestHandler<GetDashboardDataCommand, GetDashboardDataCommandResponse>
{
    private readonly DatabaseContext _databaseContext;
    private readonly PanelQueryExecutor _panelQueryExecutor;
    private readonly ILogger<GetDashboardDataCommandHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GetDashboardDataCommandHandler"/> class.
    /// </summary>
    public GetDashboardDataCommandHandler(DatabaseContext databaseContext, PanelQueryExecutor panelQueryExecutor, ILogger<GetDashboardDataCommandHandler> logger)
    {
        _databaseContext = databaseContext;
        _panelQueryExecutor = panelQueryExecutor;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<GetDashboardDataCommandResponse> Handle(GetDashboardDataCommand request, CancellationToken cancellationToken)
    {
        var dashboard = await _databaseContext.Dashboards.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && x.Kind == "view", cancellationToken)
            ?? throw BusinessException.Throw(404, "仪表盘不存在");

        var response = new GetDashboardDataCommandResponse();
        DashboardConfig config;
        try
        {
            var configJson = string.IsNullOrEmpty(dashboard.ConfigJson) ? "{}" : dashboard.ConfigJson;
            config = JsonSerializer.Deserialize<DashboardConfig>(configJson, DashboardJson.Options)
                ?? new DashboardConfig();
        }
        catch (JsonException exception)
        {
            throw BusinessException.Throw(400, "仪表盘配置 JSON 无效：" + exception.Message);
        }

        var (from, to) = PanelRange.Resolve(config.Time, request.From, request.To);
        foreach (var panel in config.Panels.SelectMany(panel => panel.Type == PanelTypes.Group ? panel.Panels ?? [] : [panel]))
        {
            response.Panels.Add(await _panelQueryExecutor.ExecuteAsync(panel, from, to, request.RowLimit, cancellationToken));
        }

        return response;
    }
}

/// <summary>
/// 面板查询预览处理（编辑器实时预览，无需先保存）.
/// </summary>
public class PreviewPanelCommandHandler : IRequestHandler<PreviewPanelCommand, PanelData>
{
    private readonly PanelQueryExecutor _panelQueryExecutor;
    private readonly ILogger<PreviewPanelCommandHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreviewPanelCommandHandler"/> class.
    /// </summary>
    public PreviewPanelCommandHandler(PanelQueryExecutor panelQueryExecutor, ILogger<PreviewPanelCommandHandler> logger)
    {
        _panelQueryExecutor = panelQueryExecutor;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<PanelData> Handle(PreviewPanelCommand request, CancellationToken cancellationToken)
    {
        var (from, to) = PanelRange.Resolve(null, request.From, request.To);
        return await _panelQueryExecutor.ExecuteAsync(request.Panel, from, to, request.RowLimit, cancellationToken);
    }
}
