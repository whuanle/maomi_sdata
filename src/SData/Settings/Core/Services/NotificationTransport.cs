using System.Net;
using SData.Infra.Exceptions;

namespace SData.Settings.Services;

/// <summary>通知发送传输层：目标地址不限制（用户配置什么就发送什么）；禁重定向、不使用代理，抑制 HTTP 诊断埋点避免含 Token 的请求泄漏.</summary>
public class NotificationTransport
{
    private static readonly SemaphoreSlim Slots = new(8);

    /// <summary>目标地址仅做形状校验（HTTP/HTTPS、无用户信息与片段），不做目的地限制.</summary>
    public void Validate(Uri uri)
    {
        if (uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
        {
            throw BusinessException.Throw(400, "通知地址必须为无用户信息及片段的 HTTP/HTTPS 地址");
        }
    }

    public virtual async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Validate(request.RequestUri!);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await Slots.WaitAsync(timeout.Token);
        try
        {
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None,
            };
            // 直接调用传输处理器，避免全局 HttpClient 插桩记录含 Token 的 URI.
            using var invoker = new HttpMessageInvoker(handler);
            using var suppression = OpenTelemetry.SuppressInstrumentationScope.Begin();
            using var response = await invoker.SendAsync(request, timeout.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > 65536) throw new HttpRequestException("通知响应过大");
                buffer.Write(chunk, 0, read);
            }

            return (response.StatusCode, System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        }
        finally
        {
            Slots.Release();
        }
    }
}
