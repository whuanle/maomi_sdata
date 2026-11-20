using SData.Infra.Configuration;
using SData.Settings.Services;
using Xunit;

namespace SData.Ingest.Tests;

public class MqSecretsTests
{
    [Fact]
    public void MaskedRoundTripRetainsCredentials()
    {
        var credential = Guid.NewGuid().ToString("N");
        var previous = new MqInstanceOptions
        {
            Uri = new UriBuilder("amqp", "localhost", 5672) { UserName = "test", Password = credential }.Uri.ToString(),
            SaslPassword = credential,
            AdminPassword = credential,
        };
        var view = new MqInstanceOptions
        {
            Uri = previous.Uri, SaslPassword = previous.SaslPassword, AdminPassword = previous.AdminPassword,
        };
        MqSecrets.Hide(view);
        Assert.DoesNotContain(credential, view.Uri);
        Assert.Equal(MqSecrets.Mask, view.SaslPassword);
        Assert.Equal(MqSecrets.Mask, view.AdminPassword);
        MqSecrets.Restore(view, previous);
        Assert.Equal(previous.SaslPassword, view.SaslPassword);
        Assert.Equal(previous.AdminPassword, view.AdminPassword);
        Assert.Equal(new Uri(previous.Uri), new Uri(view.Uri));
    }
}
