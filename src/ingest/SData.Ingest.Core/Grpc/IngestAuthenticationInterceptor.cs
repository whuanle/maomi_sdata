using Grpc.Core;
using Grpc.Core.Interceptors;
using SData.Ingest.Api;

namespace SData.Ingest.OtlpGrpc;

/// <summary>在解析及入队之前校验三个 OTLP Export 的接入凭据.</summary>
public class IngestAuthenticationInterceptor(IIngestKeyValidator validator) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var key = IngestApiKeyFilter.ExtractApiKey(context.GetHttpContext().Request.Headers);
        if (!await validator.ValidateAsync(key ?? string.Empty))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "接入鉴权失败"));
        return await continuation(request, context);
    }
}
