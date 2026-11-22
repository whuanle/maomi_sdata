using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SData.Database;

namespace SData.Rum.Services;

/// <summary>基于项目允许站点的来源策略（校验目标项目配置的 DomainsJson，无全局上限）.</summary>
public class RumOriginPolicy(IServiceScopeFactory scopeFactory) : IRumOriginPolicy
{
    /// <inheritdoc/>
    public bool Allows(string projectKey, string? origin)
    {
        var normalized = AllowedOrigin(origin);
        if (normalized == null || string.IsNullOrEmpty(projectKey))
        {
            return false;
        }

        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var json = database.RumProjects.AsNoTracking().Where(x => x.ProjectKey == projectKey)
            .Select(x => x.DomainsJson).FirstOrDefault();
        return RumOrigins.TryParse(json, out var origins) && origins.Contains(normalized, StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public bool AllowsAny(string? origin)
    {
        var normalized = AllowedOrigin(origin);
        if (normalized == null)
        {
            return false;
        }

        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        return database.RumProjects.AsNoTracking().Select(x => x.DomainsJson).AsEnumerable()
            .Any(json => RumOrigins.TryParse(json, out var origins) && origins.Contains(normalized, StringComparer.Ordinal));
    }

    private string? AllowedOrigin(string? origin)
    {
        return RumOrigins.Normalize(origin);
    }
}
