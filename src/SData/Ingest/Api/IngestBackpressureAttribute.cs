using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SData.Store;

namespace SData.Ingest.Api;

/// <summary>
/// 接入背压过滤器：在请求体反序列化（模型绑定）之前按缓冲水位快速拒绝，
/// 过载时被拒请求零反序列化开销（授权阶段运行，先于 [FromBody] 绑定）.
/// 依赖在请求期从 RequestServices 解析；宿主未注册接入缓冲的最小宿主自动跳过.
/// 拒绝语义：整批未解析、未接收（accepted=0），客户端应整批重试（Retry-After）.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class IngestBackpressureAttribute : Attribute, IAsyncAuthorizationFilter, IOrderedFilter
{
    private const int FullMargin = 64;

    /// <summary>鉴权（默认 0）之后执行，坏 key 先返回 401.</summary>
    public int Order => 100;

    /// <inheritdoc/>
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsPost(request.Method))
        {
            return Task.CompletedTask;
        }

        var services = context.HttpContext.RequestServices;
        if (services.GetService(typeof(IIngestBuffer)) is not IIngestBuffer buffer)
        {
            return Task.CompletedTask;
        }

        var full = buffer.ApproximateBufferedCount >= buffer.Capacity - FullMargin;
        var overByteBudget = request.ContentLength is long length && !buffer.CanAcceptBytes(length);
        if (!full && !overByteBudget)
        {
            return Task.CompletedTask;
        }

        (services.GetService(typeof(IStorePerformanceMetrics)) as IStorePerformanceMetrics)?.RecordIngestFastRejected(1);
        context.HttpContext.Response.Headers["Retry-After"] = "1";
        context.Result = new ObjectResult(new IngestResponse { Accepted = 0, Dropped = 0 })
        {
            StatusCode = 429,
        };
        return Task.CompletedTask;
    }
}
