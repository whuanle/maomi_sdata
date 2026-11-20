using global::Xunit;
using Microsoft.AspNetCore.Mvc;
using SData.Monitor.Api;

namespace SData.Monitor.Tests;

public class MonitorControllerTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/api/health")]
    public void Health_SupportsProbeAndApiClientRoutes(string route)
    {
        var method = typeof(MonitorController).GetMethod(nameof(MonitorController.Health))!;
        var routes = method.GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>();
        Assert.Contains(routes, attribute => attribute.Template == route);
    }
}
