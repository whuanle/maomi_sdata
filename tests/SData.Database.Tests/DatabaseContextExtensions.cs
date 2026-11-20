using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SData.Database;

namespace SData.Database.Tests;

/// <summary>
/// DatabaseContext 扩展.
/// </summary>
public static class DatabaseContextExtensions
{
    /// <summary>
    /// 判断表是否存在.
    /// </summary>
    /// <param name="context">上下文.</param>
    /// <param name="tableName">表名.</param>
    /// <returns>是否存在.</returns>
    public static bool TableExists(this DatabaseContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=@name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }
}
