using SData.Infra.Configuration;

namespace SData.Settings.Services;

/// <summary>MQ 连接凭据的展示掩码与保存恢复.</summary>
public static class MqSecrets
{
    public const string Mask = "********";

    public static void Hide(MqInstanceOptions instance)
    {
        if (!string.IsNullOrEmpty(instance.SaslPassword)) instance.SaslPassword = Mask;
        if (!string.IsNullOrEmpty(instance.AdminPassword)) instance.AdminPassword = Mask;
        if (System.Uri.TryCreate(instance.Uri, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            var builder = new UriBuilder(uri) { Password = Mask };
            instance.Uri = builder.Uri.ToString();
        }
    }

    public static void Restore(MqInstanceOptions instance, MqInstanceOptions? previous)
    {
        if (instance.SaslPassword == Mask) instance.SaslPassword = previous?.SaslPassword ?? string.Empty;
        if (instance.AdminPassword == Mask) instance.AdminPassword = previous?.AdminPassword ?? string.Empty;
        if (System.Uri.TryCreate(instance.Uri, UriKind.Absolute, out var uri)
            && Uri.UnescapeDataString(uri.UserInfo).EndsWith(":" + Mask, StringComparison.Ordinal)
            && previous != null && System.Uri.TryCreate(previous.Uri, UriKind.Absolute, out var oldUri))
        {
            var old = new UriBuilder(oldUri);
            instance.Uri = new UriBuilder(uri) { Password = old.Password }.Uri.ToString();
        }
    }
}
