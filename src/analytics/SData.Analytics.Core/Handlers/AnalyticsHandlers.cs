using MediatR;
using Microsoft.EntityFrameworkCore;
using SData.Database;
using SData.Infra.Exceptions;
using SData.Infra.Models;
using SData.Infra.Services;
using SData.Store;

namespace SData.Analytics.Handlers;

/// <summary>分析表目录处理.</summary>
public class QueryAnalyticsTablesCommandHandler(IAnalyticsCatalog catalog)
    : IRequestHandler<QueryAnalyticsTablesCommand, IReadOnlyList<AnalyticsTableItem>>
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<AnalyticsTableItem>> Handle(QueryAnalyticsTablesCommand request, CancellationToken cancellationToken)
        => catalog.GetTablesAsync(cancellationToken);
}

/// <summary>
/// 执行分析查询处理.
/// </summary>
public class ExecuteQueryCommandHandler : IRequestHandler<ExecuteQueryCommand, AnalyticsQueryResponse>
{
    private readonly IAnalyticsExecutor _executor;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecuteQueryCommandHandler"/> class.
    /// </summary>
    /// <param name="executor">执行器.</param>
    public ExecuteQueryCommandHandler(IAnalyticsExecutor executor)
    {
        _executor = executor;
    }

    /// <inheritdoc/>
    public async Task<AnalyticsQueryResponse> Handle(ExecuteQueryCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _executor.ExecuteAsync(request.Sql, request.RowLimit, cancellationToken);
            return new AnalyticsQueryResponse
            {
                Columns = result.Columns,
                Rows = result.Rows,
                ElapsedMs = result.ElapsedMs,
                Truncated = result.Truncated,
            };
        }
        catch (ArgumentException exception)
        {
            throw BusinessException.Throw(400, "SQL 校验失败：" + exception.Message);
        }
    }
}

/// <summary>
/// 统一搜索 SQL 直查处理（表占位符展开 + $start/$end 参数绑定，约束同分析查询）.
/// </summary>
public class ExecuteSearchSqlCommandHandler : IRequestHandler<ExecuteSearchSqlCommand, AnalyticsQueryResponse>
{
    private readonly IAnalyticsExecutor _executor;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecuteSearchSqlCommandHandler"/> class.
    /// </summary>
    /// <param name="executor">执行器.</param>
    public ExecuteSearchSqlCommandHandler(IAnalyticsExecutor executor)
    {
        _executor = executor;
    }

    /// <inheritdoc/>
    public async Task<AnalyticsQueryResponse> Handle(ExecuteSearchSqlCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _executor.ExecuteSearchAsync(request.Sql, request.From, request.To, request.RowLimit, request.Parameters, cancellationToken);
            return new AnalyticsQueryResponse
            {
                Columns = result.Columns,
                Rows = result.Rows,
                ElapsedMs = result.ElapsedMs,
                Truncated = result.Truncated,
            };
        }
        catch (ArgumentException exception)
        {
            throw BusinessException.Throw(400, "SQL 校验失败：" + exception.Message);
        }
    }
}

/// <summary>
/// 保存查询处理.
/// </summary>
public class SaveQueryCommandHandler : IRequestHandler<SaveQueryCommand, Simple<long>>
{
    private readonly DatabaseContext _databaseContext;
    private readonly IIdProvider _idProvider;
    private readonly IAnalyticsExecutor _executor;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaveQueryCommandHandler"/> class.
    /// </summary>
    public SaveQueryCommandHandler(DatabaseContext databaseContext, IIdProvider idProvider, IAnalyticsExecutor executor)
    {
        _databaseContext = databaseContext;
        _idProvider = idProvider;
        _executor = executor;
    }

    /// <inheritdoc/>
    public async Task<Simple<long>> Handle(SaveQueryCommand request, CancellationToken cancellationToken)
    {
        if (request.Kind == "analytics")
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(request.Definition);
                var root = document.RootElement;
                var mode = root.GetProperty("mode").GetString();
                var source = root.GetProperty("source").GetString();
                var limit = root.GetProperty("rowLimit").GetInt32();
                var range = root.GetProperty("rangeMs").GetInt64();
                var sql = root.GetProperty("sql").GetString();
                var lucene = root.GetProperty("lucene").GetString();
                var parameters = root.GetProperty("parameters");
                if (root.GetProperty("version").GetInt32() != 1 || mode is not ("sql" or "lucene")
                    || source is not ("logs" or "traces" or "metrics" or "iot" or "sessions")
                    || limit is < 1 or > 10000 || range <= 0 || range > TimeSpan.FromDays(365).TotalMilliseconds
                    || sql == null || sql.Length > 200000 || lucene == null || lucene.Length > 16000
                    || parameters.ValueKind != System.Text.Json.JsonValueKind.Object
                    || parameters.EnumerateObject().Count() > 64
                    || parameters.EnumerateObject().Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p.Name, "^[a-z][a-z0-9_]*$")
                        || p.Value.ValueKind is System.Text.Json.JsonValueKind.Array or System.Text.Json.JsonValueKind.Object))
                {
                    throw new ArgumentException();
                }
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
            {
                throw BusinessException.Throw(400, "分析工作台快照格式不正确");
            }
        }

        // kind=sql 保存前先验证 SQL 可解析（只读执行 0 行验证）；kind=search 存搜索状态 JSON，不经验证器
        if (request.Kind == "sql")
        {
            try
            {
                await _executor.ExecuteAsync($"SELECT * FROM ({request.SqlText.Trim().TrimEnd(';')}) LIMIT 0", 1, cancellationToken);
            }
            catch (ArgumentException exception)
            {
                throw BusinessException.Throw(400, "SQL 校验失败：" + exception.Message);
            }
        }

        if (request.Id == 0)
        {
            var savedQuery = new Database.Entities.SavedQueryEntity
            {
                Id = _idProvider.GetLongId(),
                Name = request.Name,
                Kind = request.Kind,
                SqlText = request.Kind == "sql" ? request.SqlText : string.Empty,
                Definition = request.Kind is "search" or "analytics" ? request.Definition : string.Empty,
                Description = request.Description,
            };

            _databaseContext.SavedQueries.Add(savedQuery);
            await _databaseContext.SaveChangesAsync(cancellationToken);
            return new Simple<long> { Value = savedQuery.Id };
        }

        var existing = await _databaseContext.SavedQueries.FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken)
            ?? throw BusinessException.Throw(404, "查询不存在");

        existing.Name = request.Name;
        existing.Kind = request.Kind;
        existing.SqlText = request.Kind == "sql" ? request.SqlText : string.Empty;
        existing.Definition = request.Kind is "search" or "analytics" ? request.Definition : string.Empty;
        existing.Description = request.Description;
        await _databaseContext.SaveChangesAsync(cancellationToken);
        return new Simple<long> { Value = existing.Id };
    }
}

/// <summary>
/// 保存查询列表处理.
/// </summary>
public class QuerySavedQueriesCommandHandler : IRequestHandler<QuerySavedQueriesCommand, PagedResult<SavedQueryItem>>
{
    private readonly DatabaseContext _databaseContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuerySavedQueriesCommandHandler"/> class.
    /// </summary>
    public QuerySavedQueriesCommandHandler(DatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    /// <inheritdoc/>
    public async Task<PagedResult<SavedQueryItem>> Handle(QuerySavedQueriesCommand request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var query = _databaseContext.SavedQueries.AsNoTracking().OrderByDescending(x => x.Id);
        if (!string.IsNullOrWhiteSpace(request.Kind))
        {
            query = query.Where(x => x.Kind == request.Kind).OrderByDescending(x => x.Id);
        }

        var count = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new SavedQueryItem
            {
                Id = x.Id,
                Name = x.Name,
                Kind = x.Kind,
                SqlText = x.SqlText,
                Definition = x.Definition,
                Description = x.Description,
                CreateTime = x.CreateTime,
                UpdateTime = x.UpdateTime,
                CreateUserId = x.CreateUserId,
                UpdateUserId = x.UpdateUserId,
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<SavedQueryItem>
        {
            Page = page,
            PageSize = pageSize,
            Count = count,
            Data = items,
        };
    }
}

/// <summary>
/// 按 id 查询保存项处理.
/// </summary>
public class GetSavedQueryCommandHandler : IRequestHandler<GetSavedQueryCommand, SavedQueryItem>
{
    private readonly DatabaseContext _databaseContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="GetSavedQueryCommandHandler"/> class.
    /// </summary>
    public GetSavedQueryCommandHandler(DatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    /// <inheritdoc/>
    public async Task<SavedQueryItem> Handle(GetSavedQueryCommand request, CancellationToken cancellationToken)
    {
        var savedQuery = await _databaseContext.SavedQueries.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken)
            ?? throw BusinessException.Throw(404, "查询不存在");

        return new SavedQueryItem
        {
            Id = savedQuery.Id,
            Name = savedQuery.Name,
            Kind = savedQuery.Kind,
            SqlText = savedQuery.SqlText,
            Definition = savedQuery.Definition,
            Description = savedQuery.Description,
            CreateTime = savedQuery.CreateTime,
            UpdateTime = savedQuery.UpdateTime,
            CreateUserId = savedQuery.CreateUserId,
            UpdateUserId = savedQuery.UpdateUserId,
        };
    }
}

/// <summary>
/// 删除保存查询处理.
/// </summary>
public class DeleteSavedQueryCommandHandler : IRequestHandler<DeleteSavedQueryCommand, EmptyCommandResponse>
{
    private readonly DatabaseContext _databaseContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeleteSavedQueryCommandHandler"/> class.
    /// </summary>
    public DeleteSavedQueryCommandHandler(DatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    /// <inheritdoc/>
    public async Task<EmptyCommandResponse> Handle(DeleteSavedQueryCommand request, CancellationToken cancellationToken)
    {
        var savedQuery = await _databaseContext.SavedQueries.FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken)
            ?? throw BusinessException.Throw(404, "查询不存在");

        if (await _databaseContext.AlertRules.AnyAsync(x => x.SavedQueryId == request.Id, cancellationToken))
        {
            throw BusinessException.Throw(409, "请先删除关联告警，再删除保存的搜索");
        }

        _databaseContext.SavedQueries.Remove(savedQuery);
        await _databaseContext.SaveChangesAsync(cancellationToken);
        return new EmptyCommandResponse();
    }
}

/// <summary>
/// 执行保存查询处理.
/// </summary>
public class ExecuteSavedQueryCommandHandler : IRequestHandler<ExecuteSavedQueryCommand, AnalyticsQueryResponse>
{
    private readonly DatabaseContext _databaseContext;
    private readonly IAnalyticsExecutor _executor;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecuteSavedQueryCommandHandler"/> class.
    /// </summary>
    public ExecuteSavedQueryCommandHandler(DatabaseContext databaseContext, IAnalyticsExecutor executor)
    {
        _databaseContext = databaseContext;
        _executor = executor;
    }

    /// <inheritdoc/>
    public async Task<AnalyticsQueryResponse> Handle(ExecuteSavedQueryCommand request, CancellationToken cancellationToken)
    {
        var savedQuery = await _databaseContext.SavedQueries.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken)
            ?? throw BusinessException.Throw(404, "查询不存在");

        if (savedQuery.Kind != "sql")
        {
            // 保存的搜索（kind=search）没有 SQL 文本，执行通道只面向 SQL 查询
            throw BusinessException.Throw(400, "该保存项为搜索快照，不包含可执行 SQL");
        }

        var result = await _executor.ExecuteAsync(savedQuery.SqlText, request.RowLimit, cancellationToken);
        return new AnalyticsQueryResponse
        {
            Columns = result.Columns,
            Rows = result.Rows,
            ElapsedMs = result.ElapsedMs,
            Truncated = result.Truncated,
        };
    }
}
