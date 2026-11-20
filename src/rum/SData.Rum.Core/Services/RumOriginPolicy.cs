using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SData.Database;

namespace SData.Rum.Services;

/// <summary>基于项目允许站点与可选全局上限的来源策略.</summary>
public class RumOriginPolicy(IServiceScopeFactory scopeFactory, IConfiguration configuration) : IRumOriginPolicy
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
        var normalized = RumOrigins.Normalize(origin);
        var limit = configuration.GetSection("SData:Rum:CorsOrigins").Get<string[]>() ?? [];
        return normalized != null && (limit.Length == 0 || limit.Any(value => RumOrigins.Normalize(value) == normalized))
            ? normalized : null;
    }
}
