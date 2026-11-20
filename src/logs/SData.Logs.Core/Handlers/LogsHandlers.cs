using MediatR;
using SData.Infra.Models;
using SData.Store;

namespace SData.Logs.Handlers;

/// <summary>
/// 日志查询处理.
/// </summary>
public class QueryLogsCommandHandler : IRequestHandler<QueryLogsCommand, PagedResult<LogItem>>
{
    private readonly ILogStore _logStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryLogsCommandHandler"/> class.
    /// </summary>
    /// <param name="logStore">日志存储.</param>
    public QueryLogsCommandHandler(ILogStore logStore)
    {
        _logStore = logStore;
    }

    /// <inheritdoc/>
    public async Task<PagedResult<LogItem>> Handle(QueryLogsCommand request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);

        var records = await _logStore.QueryAsync(new Store.Models.LogQuery
        {
            Start = request.Start ?? default,
            End = request.End ?? default,
            Service = request.Service,
            Severity = request.Severity,
            Search = request.Search,
            Limit = pageSize,
        }, cancellationToken);

        // 按（页码-1）* 页大小跳过内存中的行（DuckDB 查询已按时间倒序 + limit 截断）
        var items = records
            .Skip((page - 1) * pageSize)
            .Select(record => new LogItem
            {
                Timestamp = record.Timestamp,
                Service = record.Service,
                Severity = record.Severity,
                Body = record.Body,
                TraceId = record.TraceId,
                SpanId = record.SpanId,
                Attributes = record.AttributesJson,
                Resource = record.ResourceJson,
            })
            .ToList();

        return new PagedResult<LogItem>
        {
            Page = page,
            PageSize = pageSize,
            Count = -1,
            Data = items,
        };
    }
}

/// <summary>
/// 服务名列表处理.
/// </summary>
public class QueryLogServicesCommandHandler : IRequestHandler<QueryLogServicesCommand, List<string>>
{
    private readonly ILogStore _logStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryLogServicesCommandHandler"/> class.
    /// </summary>
    /// <param name="logStore">日志存储.</param>
    public QueryLogServicesCommandHandler(ILogStore logStore)
    {
        _logStore = logStore;
    }

    /// <inheritdoc/>
    public async Task<List<string>> Handle(QueryLogServicesCommand request, CancellationToken cancellationToken)
    {
        var services = await _logStore.ListServicesAsync(cancellationToken);
        return services.ToList();
    }
}

/// <summary>
/// 日志统一查询处理（DSL，MaxQueryDays 约束在执行器内）.
/// </summary>
public class SearchLogsCommandHandler : IRequestHandler<SearchLogsCommand, SData.Store.QueryDsl.SearchDslResponse>
{
    private readonly SData.Store.QueryDsl.IQueryDslExecutor _executor;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchLogsCommandHandler"/> class.
    /// </summary>
    /// <param name="executor">DSL 执行器.</param>
    public SearchLogsCommandHandler(SData.Store.QueryDsl.IQueryDslExecutor executor)
    {
        _executor = executor;
    }

    /// <inheritdoc/>
    public Task<SData.Store.QueryDsl.SearchDslResponse> Handle(SearchLogsCommand request, CancellationToken cancellationToken)
    {
        return _executor.ExecuteAsync(request, SData.Store.QueryDsl.DataDomain.Logs, cancellationToken);
    }
}
