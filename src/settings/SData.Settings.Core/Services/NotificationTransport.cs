using System.Net;
using System.Net.Sockets;
using SData.Infra.Configuration;
using SData.Infra.Exceptions;

namespace SData.Settings.Services;

/// <summary>禁重定向、目标固定与连接时地址检查；不使用代理及 HTTP 诊断事件泄漏凭据.</summary>
public class NotificationTransport(SystemOptions options)
{
    private static readonly SemaphoreSlim Slots = new(8);
    private static readonly Dictionary<string, string> PublicHosts = new()
    {
        ["telegram"] = "api.telegram.org",
        ["discord"] = "discord.com",
        ["feishu"] = "open.feishu.cn",
        ["dingtalk"] = "oapi.dingtalk.com",
        ["wecom"] = "qyapi.weixin.qq.com",
        ["serverchan"] = "sctapi.ftqq.com",
    };

    public void Validate(string type, Uri uri)
    {
        if (uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
        {
            throw BusinessException.Throw(400, "通知地址必须为无用户信息及片段的 HTTP/HTTPS 地址");
        }

        var official = PublicHosts.TryGetValue(type, out var host) && uri.Scheme == "https" && uri.IsDefaultPort && uri.IdnHost == host;
        var builtIn = (type == "bark" && uri.IdnHost == "api.day.app")
            || (type == "pushdeer" && uri.IdnHost == "api2.pushdeer.com");
        if (!official && !(builtIn && uri.Scheme == "https" && uri.IsDefaultPort) && !IsAllowed(uri))
        {
            throw BusinessException.Throw(400, "通知目标未在 SData:NotificationAllowedOrigins 白名单中");
        }

        if (PublicHosts.ContainsKey(type) && !official)
        {
            throw BusinessException.Throw(400, "该通知类型仅支持官方 HTTPS 服务");
        }
    }

    public virtual async Task<(HttpStatusCode Status, string Body)> SendAsync(string type, HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        Validate(type, uri);
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
                ConnectCallback = async (context, ct) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                    var allowPrivate = IsAllowed(uri);
                    foreach (var address in addresses)
                    {
                        if (IsForbidden(address, allowPrivate))
                        {
                            throw BusinessException.Throw(400, "通知目标解析到禁止访问的地址");
                        }
                    }

                    foreach (var address in addresses)
                    {
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            if (ct.IsCancellationRequested) throw;
                        }
                    }

                    throw new HttpRequestException("通知目标连接失败");
                },
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

    public static bool IsForbidden(IPAddress address, bool allowPrivate)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Parse("168.63.129.16")) || address.Equals(IPAddress.Parse("fd00:ec2::254"))) return true;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (b[0] == 0 || b[0] == 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)) return true;
            return !allowPrivate && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return true;
        // 仅原生全局单播或显式放行的 ULA，拒绝转换/隧道地址.
        return b[0] is not (>= 0x20 and <= 0x3f) && !(allowPrivate && (b[0] & 0xfe) == 0xfc)
            || (b[0] == 0x20 && b[1] == 0x02) || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0);
    }

    private bool IsAllowed(Uri uri) => options.NotificationAllowedOrigins.Any(value =>
        Uri.TryCreate(value, UriKind.Absolute, out var allowed)
        && allowed.Scheme == uri.Scheme && allowed.IdnHost == uri.IdnHost && allowed.Port == uri.Port
        && allowed.UserInfo.Length == 0 && allowed.AbsolutePath == "/" && allowed.Query.Length == 0 && allowed.Fragment.Length == 0);
}
