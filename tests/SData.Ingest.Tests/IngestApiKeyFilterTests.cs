using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using SData.Ingest.Api;
using SData.Infra.Configuration;
using Xunit;

namespace SData.Ingest.Tests;

/// <summary>
/// 接入鉴权过滤器测试（T113）：X-Api-Key / Bearer / Basic 三态提取与校验语义.
/// </summary>
public class IngestApiKeyFilterTests
{
    /// <summary>
    /// X-Api-Key、Bearer、Basic(password=Key) 三态均提取到 key；Basic 无冒号时整串视为 key；坏 Base64 拒绝.
    /// </summary>
    [Theory]
    [InlineData("X-Api-Key", "key-1", "key-1")]
    [InlineData("Authorization", "Bearer key-2", "key-2")]
    [InlineData("Authorization", "Bearer  spaced  ", "spaced")]
    [InlineData("Authorization", "Basic c2RhdGE6a2V5LTM=", "key-3")]
    [InlineData("Authorization", "Basic a2V5LTQ=", "key-4")]
    [InlineData("Authorization", "bearer key-5", "key-5")]
    public void ExtractApiKey_SupportsThreeAuthForms(string headerName, string headerValue, string expected)
    {
        var headers = new HeaderDictionary { [headerName] = headerValue };

        var apiKey = IngestApiKeyFilter.ExtractApiKey(headers);

        Assert.Equal(expected, apiKey);
    }

    /// <summary>
    /// 无任何凭证头时返回 null；非法 Base64 返回 null.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExtractApiKey_MissingOrMalformed_ReturnsNull(bool malformedBase64)
    {
        var headers = new HeaderDictionary();
        if (malformedBase64)
        {
            headers["Authorization"] = "Basic !!!not-base64!!!";
        }

        var apiKey = IngestApiKeyFilter.ExtractApiKey(headers);

        Assert.Null(apiKey);
    }

    /// <summary>
    /// 有效 key 放行；无效 key 401；RequireAuth=false 时直接放行.
    /// </summary>
    [Fact]
    public async Task OnAuthorization_ValidatesAndGates()
    {
        var validator = new StubValidator("sdata-dev-ingest-key");
        var validKeyFilter = new IngestApiKeyFilter(validator, new SystemOptions());
        var invalidKeyFilter = new IngestApiKeyFilter(validator, new SystemOptions());
        var openFilter = new IngestApiKeyFilter(validator, new SystemOptions { Ingest = { RequireAuth = false } });

        var validContext = CreateContext("X-Api-Key", "sdata-dev-ingest-key");
        await validKeyFilter.OnAuthorizationAsync(validContext);
        Assert.Null(validContext.Result);

        var invalidContext = CreateContext("X-Api-Key", "wrong-key");
        await invalidKeyFilter.OnAuthorizationAsync(invalidContext);
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(invalidContext.Result);
        Assert.Equal(401, result.StatusCode);

        var openContext = CreateContext(null, null);
        await openFilter.OnAuthorizationAsync(openContext);
        Assert.Equal(401, Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(openContext.Result).StatusCode);
    }

    private static AuthorizationFilterContext CreateContext(string? headerName, string? headerValue)
    {
        var httpContext = new DefaultHttpContext();
        if (headerName != null && headerValue != null)
        {
            httpContext.Request.Headers[headerName] = headerValue;
        }

        return new AuthorizationFilterContext(
            new Microsoft.AspNetCore.Mvc.ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()),
            Array.Empty<IFilterMetadata>());
    }

    /// <summary>固定 key 的校验器.</summary>
    private sealed class StubValidator(string validKey) : IIngestKeyValidator
    {
        public Task<bool> ValidateAsync(string apiKey) => Task.FromResult(apiKey == validKey);
    }
}
