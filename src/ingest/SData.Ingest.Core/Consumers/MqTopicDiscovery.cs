using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SData.Infra.Configuration;

namespace SData.Ingest.Consumers;

/// <summary>
/// 管理 API 主题发现；仅在显式配置的 vhost/namespace 内匹配前缀，不创建或修改资源.
/// </summary>
public static class MqTopicDiscovery
{
    /// <summary>返回已解析为真实主题名的规则快照.</summary>
    public static async Task<List<MqTopicOptions>> DiscoverAsync(MqInstanceOptions instance, CancellationToken cancellationToken)
    {
        var names = instance.Topics.Where(t => t.MatchMode == "exact").Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (instance.Topics.Any(t => t.MatchMode == "prefix"))
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(10),
                MaxResponseContentBufferSize = 4 * 1024 * 1024,
            };
            string path;
            if (instance.Type == "rabbitmq")
            {
                var amqp = new Uri(instance.Uri);
                var vhost = Uri.UnescapeDataString(amqp.AbsolutePath.TrimStart('/'));
                if (vhost.Length == 0)
                {
                    vhost = "/";
                }

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(instance.AdminUsername + ":" + instance.AdminPassword)));
                path = "api/queues/" + Uri.EscapeDataString(vhost);
            }
            else
            {
                if (!string.IsNullOrEmpty(instance.AdminPassword))
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", instance.AdminPassword);
                }

                path = "admin/v2/persistent/" + string.Join("/", instance.Namespace.Split('/').Select(Uri.EscapeDataString));
            }

            using var response = await client.GetAsync(instance.AdminUrl.TrimEnd('/') + "/" + path, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var name = instance.Type == "rabbitmq" ? item.GetProperty("name").GetString() : item.GetString();
                if (name != null && MqTopicMatcher.Match(instance.Topics, name) != null)
                {
                    names.Add(name);
                }
            }
        }

        return names.Order(StringComparer.Ordinal)
            .Select(name => (Name: name, Rule: MqTopicMatcher.Match(instance.Topics, name)))
            .Where(item => item.Rule != null)
            .Select(item => MqTopicMatcher.ForTopic(item.Rule!, item.Name))
            .ToList();
    }
}
