using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SData.Database;
using SData.Infra.Services;
using SData.Settings;

namespace SData.Agents;

/// <summary>每个协议请求重新鉴权；限制每凭据速率和并发，避免长连接绕过撤销（预算取运行参数，每请求读取）.</summary>
public class AgentRequestBudget(ISystemSettingsProvider settings)
{
    private readonly ISystemSettingsProvider _settings = settings;
    private readonly object _sync = new();
    private readonly Dictionary<long, Entry> _entries = new();
    private int _active;
    private sealed class Entry
    {
        public long Window;
        public int Count;
        public int Active;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.ContentLength > 64 * 1024) { context.Response.StatusCode = 413; return; }
        var bodyFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (bodyFeature is { IsReadOnly: false }) bodyFeature.MaxRequestBodySize = 64 * 1024;
        if (context.Request.Headers.Origin.Count > 0)
        {
            if (context.Request.Headers.Origin.Count != 1
                || !Uri.TryCreate(context.Request.Headers.Origin[0], UriKind.Absolute, out var origin)
                || origin.GetLeftPart(UriPartial.Authority) != context.Request.Scheme + "://" + context.Request.Host)
            { context.Response.StatusCode = 403; return; }
        }
        var authorization = context.Request.Headers.Authorization;
        if (authorization.Count != 1 || !authorization[0]!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        { context.Response.StatusCode = 401; return; }
        var key = authorization[0]![7..].Trim();
        if (!key.StartsWith("sda_", StringComparison.Ordinal) || key.Length != 68)
        { context.Response.StatusCode = 401; return; }
        var db = context.RequestServices.GetRequiredService<DatabaseContext>();
        var hash = HashHelper.Sha256Hex(key);
        var id = await db.AgentKeys.AsNoTracking().Where(x => x.Enabled && x.KeyHash == hash)
            .Select(x => (long?)x.Id).SingleOrDefaultAsync(context.RequestAborted);
        if (id == null) { context.Response.StatusCode = 401; return; }
        Entry entry;
        var window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var limits = _settings.GetRuntime().Agents;
        lock (_sync)
        {
            foreach (var expired in _entries.Where(x => x.Value.Active == 0 && x.Value.Window < window - 1).Select(x => x.Key).ToArray())
                _entries.Remove(expired);
            if (!_entries.TryGetValue(id.Value, out entry!))
            {
                if (_entries.Count >= 4096) { context.Response.StatusCode = 429; return; }
                _entries[id.Value] = entry = new Entry();
            }
            if (entry.Window != window) { entry.Window = window; entry.Count = 0; }
            if (entry.Count >= Math.Clamp(limits.RequestsPerMinute, 1, 600) || entry.Active >= Math.Clamp(limits.PerKeyConcurrency, 1, 4) || _active >= Math.Clamp(limits.GlobalConcurrency, 1, 16))
            { context.Response.StatusCode = 429; return; }
            entry.Count++;
            entry.Active++;
            _active++;
        }
        context.Items["AgentKeyId"] = id.Value;
        using var audit = context.RequestServices.GetService<Microsoft.Extensions.Logging.ILogger<AgentRequestBudget>>()?.BeginScope(new Dictionary<string, object> { ["AgentKeyId"] = id.Value });
        try { await next(context); }
        finally { lock (_sync) { entry.Active--; _active--; } }
    }
}
