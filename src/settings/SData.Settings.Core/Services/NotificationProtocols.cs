using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SData.Settings.Services;

/// <summary>文本通知协议适配；JSON 模板仅替换字符串值，不执行表达式.</summary>
public static class NotificationProtocols
{
    public static HttpRequestMessage Build(string type, Dictionary<string, string> config, NotificationMessage message)
    {
        string Get(string key) => config.GetValueOrDefault(key, string.Empty);
        var text = message.Title + "\n" + message.Body + (string.IsNullOrEmpty(message.Link) ? "" : "\n" + message.Link);
        HttpRequestMessage Json(string url, object body) => new(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        var server = Get("server").TrimEnd('/');
        switch (type)
        {
            case "telegram":
                return Json($"https://api.telegram.org/bot{Get("token")}/sendMessage", new { chat_id = Get("chatId"), text = Limit(text, 4000) });
            case "discord":
                return Json(Get("webhook"), new { content = Limit(text, 1900), allowed_mentions = new { parse = Array.Empty<string>() } });
            case "feishu":
                var feishu = new Dictionary<string, object> { ["msg_type"] = "text", ["content"] = new { text = Limit(text, 10000) } };
                if (!string.IsNullOrEmpty(Get("secret")))
                {
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                    feishu["timestamp"] = timestamp;
                    feishu["sign"] = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(timestamp + "\n" + Get("secret")), Array.Empty<byte>()));
                }

                return Json(Get("webhook"), feishu);
            case "dingtalk":
                var url = Get("webhook");
                if (!string.IsNullOrEmpty(Get("secret")))
                {
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                    var sign = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Get("secret")), Encoding.UTF8.GetBytes(timestamp + "\n" + Get("secret"))));
                    url += (url.Contains('?') ? "&" : "?") + "timestamp=" + timestamp + "&sign=" + Uri.EscapeDataString(sign);
                }

                return Json(url, new { msgtype = "text", text = new { content = Limit(text, 10000) } });
            case "wecom":
                return Json(Get("webhook"), new { msgtype = "text", text = new { content = Limit(text, 1800) } });
            case "bark":
                return Json(server + "/push", new { device_key = Get("token"), title = message.Title, body = message.Body, url = message.Link });
            case "gotify":
                var gotify = Json(server + "/message", new { title = message.Title, message = text, priority = message.Level == "error" ? 8 : 5 });
                gotify.Headers.TryAddWithoutValidation("X-Gotify-Key", Get("token"));
                return gotify;
            case "serverchan":
                return new HttpRequestMessage(HttpMethod.Post, $"https://sctapi.ftqq.com/{Get("token")}.send")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["title"] = Limit(message.Title, 64), ["desp"] = text }),
                };
            case "pushdeer":
                return new HttpRequestMessage(HttpMethod.Post, server + "/message/push")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["pushkey"] = Get("token"), ["text"] = message.Title, ["desp"] = message.Body, ["type"] = "text" }),
                };
            case "matrix":
                var matrix = Json(server + "/_matrix/client/v3/rooms/" + Uri.EscapeDataString(Get("roomId")) + "/send/m.room.message/" + Guid.NewGuid().ToString("N"), new { msgtype = "m.text", body = text });
                matrix.Method = HttpMethod.Put;
                matrix.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Get("token"));
                return matrix;
            case "webhook":
                var template = Get("template");
                JsonNode? body = string.IsNullOrWhiteSpace(template)
                    ? JsonSerializer.SerializeToNode(message, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    : JsonNode.Parse(template);
                Replace(body, new Dictionary<string, string>
                {
                    ["{{title}}"] = message.Title, ["{{body}}"] = message.Body, ["{{level}}"] = message.Level,
                    ["{{time}}"] = message.Time.ToString("O"), ["{{link}}"] = message.Link ?? string.Empty,
                });
                var custom = Json(Get("webhook"), body!);
                foreach (var header in JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(Get("headers")) ? "{}" : Get("headers"))!)
                {
                    custom.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                return custom;
            default:
                throw new InvalidOperationException("不支持的通知类型");
        }
    }

    public static bool IsSuccess(string type, int status, string body)
    {
        if (status < 200 || status >= 300) return false;
        if (type is "discord" or "webhook") return true;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            bool Zero(string key) => root.TryGetProperty(key, out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value == 0;
            return type switch
            {
                "telegram" => root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                "feishu" => Zero("code") || Zero("StatusCode"),
                "dingtalk" or "wecom" => Zero("errcode"),
                "bark" => root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value == 200,
                "serverchan" or "pushdeer" => Zero("code"),
                "gotify" => root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number,
                "matrix" => root.TryGetProperty("event_id", out var eventId) && eventId.ValueKind == JsonValueKind.String,
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)];

    private static void Replace(JsonNode? node, Dictionary<string, string> values)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text)) obj[key] = Substitute(text, values);
                else Replace(obj[key], values);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text)) array[index] = Substitute(text, values);
                else Replace(array[index], values);
            }
        }
    }

    private static string Substitute(string text, Dictionary<string, string> values)
    {
        // 单次扫描，不递归解释消息正文内的占位符.
        return System.Text.RegularExpressions.Regex.Replace(text, @"\{\{(title|body|level|time|link)\}\}", match => values[match.Value]);
    }
}
