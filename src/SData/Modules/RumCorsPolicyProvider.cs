using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using SData.Rum;
using SData.Rum.Api;

namespace SData;

/// <summary>仅为 RUM 端点生成动态来源策略；实际采集仍按目标项目校验.</summary>
public class RumCorsPolicyProvider(IOptions<CorsOptions> options, IRumOriginPolicy origins) : ICorsPolicyProvider
{
    private readonly DefaultCorsPolicyProvider _fallback = new(options);

    /// <inheritdoc/>
    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        if (policyName != ConfigureSignalRModule.RumCorsPolicy)
        {
            return _fallback.GetPolicyAsync(context, policyName);
        }

        var policy = new CorsPolicyBuilder()
            .SetIsOriginAllowed(origin => origins.AllowsAny(origin) ||
                (context.Request.Path.StartsWithSegments("/hub/rum") && RumRequestOrigin.IsSameOrigin(context.Request, RumOrigins.Normalize(origin))))
            .AllowCredentials().AllowAnyHeader().WithMethods("GET", "POST").Build();
        return Task.FromResult<CorsPolicy?>(policy);
    }
}
