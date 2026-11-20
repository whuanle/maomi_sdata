using MediatR;
using SData.Rum.Models;
using SData.Store;

namespace SData.Rum.Handlers;

/// <summary>分页会话检索处理.</summary>
public class SearchRumSessionsCommandHandler(IRumStore store) : IRequestHandler<SearchRumSessionsCommand, RumSessionPage>
{
    /// <inheritdoc/>
    public Task<RumSessionPage> Handle(SearchRumSessionsCommand request, CancellationToken cancellationToken)
    {
        return store.SearchSessionsAsync(request.ProjectKey, request.From, request.To, request, cancellationToken);
    }
}

/// <summary>会话事件检索处理.</summary>
public class QueryRumSessionEventsCommandHandler(IRumStore store) : IRequestHandler<QueryRumSessionEventsCommand, IReadOnlyList<RumSessionEvent>>
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<RumSessionEvent>> Handle(QueryRumSessionEventsCommand request, CancellationToken cancellationToken)
    {
        return store.ListSessionEventsAsync(request.ProjectKey, request.SessionId, request.From, request.To, cancellationToken);
    }
}
