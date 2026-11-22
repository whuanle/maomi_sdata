using Mediator;
using SData.Rum.Models;
using SData.Store;

namespace SData.Rum.Handlers;

/// <summary>分页会话检索处理.</summary>
public class SearchRumSessionsCommandHandler(IRumStore store) : IRequestHandler<SearchRumSessionsCommand, RumSessionPage>
{
    /// <inheritdoc/>
    public async ValueTask<RumSessionPage> Handle(SearchRumSessionsCommand request, CancellationToken cancellationToken)
    {
        return await store.SearchSessionsAsync(request.ProjectKey, request.From, request.To, request, cancellationToken);
    }
}

/// <summary>会话事件检索处理.</summary>
public class QueryRumSessionEventsCommandHandler(IRumStore store) : IRequestHandler<QueryRumSessionEventsCommand, IReadOnlyList<RumSessionEvent>>
{
    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<RumSessionEvent>> Handle(QueryRumSessionEventsCommand request, CancellationToken cancellationToken)
    {
        return await store.ListSessionEventsAsync(request.ProjectKey, request.SessionId, request.From, request.To, cancellationToken);
    }
}
