using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using SData.Infra.Exceptions;

namespace SData.Settings.Services;

/// <summary>渠道配置保护与脱敏；保留字段与新值不能同时提交.</summary>
public class NotificationConfiguration(IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("SData.NotificationChannels.v1");

    public string Protect(Dictionary<string, string> config) => _protector.Protect(JsonSerializer.Serialize(config));

    public Dictionary<string, string> Unprotect(string config) => JsonSerializer.Deserialize<Dictionary<string, string>>(_protector.Unprotect(config))!;

    public Dictionary<string, string> Merge(NotificationChannelDraft command, Dictionary<string, string>? previous)
    {
        if (!NotificationTypes.Fields.TryGetValue(command.Type, out var fields) || command.Config == null || command.KeepSecrets == null)
        {
            throw BusinessException.Throw(400, "通知渠道配置无效");
        }

        if (command.Config.Keys.Any(x => !fields.Contains(x)) || command.KeepSecrets.Any(x => !fields.Contains(x) || !NotificationTypes.IsSecret(x)))
        {
            throw BusinessException.Throw(400, "通知渠道包含不支持的字段");
        }

        var result = new Dictionary<string, string>(command.Config);
        foreach (var key in command.KeepSecrets)
        {
            if (result.ContainsKey(key) || previous == null || !previous.TryGetValue(key, out var value))
            {
                throw BusinessException.Throw(400, "无法保留通知凭据，请重新输入");
            }

            result[key] = value;
        }

        foreach (var field in fields.Where(x => x is not ("secret" or "headers" or "template")))
        {
            if (!result.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw BusinessException.Throw(400, "请填写通知渠道的必填配置");
            }
        }

        if (result.Any(x => x.Value == null || x.Value.Length > 16000))
        {
            throw BusinessException.Throw(400, "通知渠道字段过长");
        }

        if (command.Type == "telegram" && (result["token"].Any(x => !char.IsAsciiLetterOrDigit(x) && x is not (':' or '_' or '-'))))
        {
            throw BusinessException.Throw(400, "Telegram Token 格式无效");
        }

        if (command.Type == "serverchan" && result["token"].Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('_' or '-')))
        {
            throw BusinessException.Throw(400, "Server Chan SendKey 格式无效");
        }

        foreach (var field in new[] { "server", "webhook" }.Where(result.ContainsKey))
        {
            if (!Uri.TryCreate(result[field], UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || (field == "server" && uri.Query.Length != 0))
            {
                throw BusinessException.Throw(400, "通知服务地址格式无效");
            }
        }

        if (result.Values.Any(value => value.Contains('\r') || value.Contains('\n')) && command.Type != "webhook")
        {
            throw BusinessException.Throw(400, "通知配置不能包含换行");
        }

        if (command.Type == "webhook")
        {
            var template = result.GetValueOrDefault("template");
            ValidateJson(string.IsNullOrWhiteSpace(template) ? "{}" : template);
            Dictionary<string, string>? headers;
            try
            {
                var json = result.GetValueOrDefault("headers");
                headers = JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            }
            catch (JsonException)
            {
                throw BusinessException.Throw(400, "自定义请求头 JSON 无效");
            }
            if (headers == null || headers.Count > 16 || headers.Any(x => !AllowedHeader(x.Key) || x.Value == null || x.Value.Length > 4096 || x.Value.Any(c => c is '\r' or '\n')))
            {
                throw BusinessException.Throw(400, "自定义请求头无效");
            }
        }

        return result;
    }

    public static bool AllowedHeader(string key) => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || (key.StartsWith("X-", StringComparison.OrdinalIgnoreCase) && key.Length <= 64 && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    private static void ValidateJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw BusinessException.Throw(400, "通知模板必须为 JSON 对象");
            }
        }
        catch (JsonException)
        {
            throw BusinessException.Throw(400, "通知模板 JSON 无效");
        }
    }
}
