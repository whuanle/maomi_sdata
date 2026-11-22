using Microsoft.EntityFrameworkCore;
using SData.Database;

namespace SData.Database.Sqlite;

/// <summary>
/// SQLite 元数据库上下文.
/// </summary>
public class SqliteDatabaseContext : DatabaseContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteDatabaseContext"/> class.
    /// </summary>
    /// <param name="options">配置.</param>
    /// <param name="userContextProvider">用户上下文.</param>
    public SqliteDatabaseContext(DbContextOptions options, SData.Infra.Services.IUserContextProvider? userContextProvider = null)
        : base(options, userContextProvider)
    {
    }
}
