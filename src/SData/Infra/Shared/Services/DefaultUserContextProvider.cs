using SData.Infra.Models;

namespace SData.Infra.Services;

/// <summary>
/// IUserContextProvider 默认实现.
/// </summary>
public class DefaultUserContextProvider : IUserContextProvider
{
    private readonly IUserContext _userContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultUserContextProvider"/> class.
    /// </summary>
    /// <param name="userContext">用户上下文.</param>
    public DefaultUserContextProvider(IUserContext userContext)
    {
        _userContext = userContext;
    }

    /// <inheritdoc/>
    public void SetUserContext<T>(T request)
        where T : IUserIdContext
    {
        request.ContextUserId = _userContext.UserId;
        request.ContextUserRole = _userContext.Role;
    }

    /// <inheritdoc/>
    public long GetCurrentUserId()
    {
        return _userContext.UserId;
    }
}
