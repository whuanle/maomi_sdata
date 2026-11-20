using System.Text;
using global::Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Configuration;
using SData.Rum;
using SData.Rum.Api;
using SData.Rum.Services;
using SData.Store.Core;
using Microsoft.AspNetCore.Http;

namespace SData.Store.Tests;

public class RumOriginPolicyTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rum-origins-{Guid.NewGuid():N}.db");
    private readonly RumOriginPolicy _policy;

    public RumOriginPolicyTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DatabaseContext>(options => options.UseSqlite($"Data Source={_path}"));
        _services = services.BuildServiceProvider();
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        db.Database.EnsureCreated();
        db.RumProjects.AddRange(
            new RumProjectEntity { Id = 1, ProjectKey = "rum_demo", Name = "demo", DomainsJson = "[\"https://app.example.com\"]", PerfSampleRate = 0.5, ReplaySampleRate = 0.2 },
            new RumProjectEntity { Id = 2, ProjectKey = "rum_empty", Name = "empty", DomainsJson = "[]" },
            new RumProjectEntity { Id = 3, ProjectKey = "rum_legacy", Name = "legacy", DomainsJson = "[\"app.example.com\"]" });
        db.SaveChanges();
        _policy = new RumOriginPolicy(_services.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build());
    }

    [Theory]
    [InlineData("https://app.example.com", true)]
    [InlineData("HTTPS://APP.EXAMPLE.COM:443", true)]
    [InlineData("http://app.example.com", false)]
    [InlineData("https://app.example.com:444", false)]
    [InlineData("https://app.example.com.evil.test", false)]
    [InlineData("https://sub.app.example.com", false)]
    [InlineData("null", false)]
    [InlineData(null, false)]
    public void ExactOrigin(string? origin, bool expected)
    {
        Assert.Equal(expected, _policy.Allows("rum_demo", origin));
    }

    [Theory]
    [InlineData("rum_missing")]
    [InlineData("rum_empty")]
    [InlineData("rum_legacy")]
    public void UnconfiguredProjectsDenied(string key) => Assert.False(_policy.Allows(key, "https://app.example.com"));

    [Fact]
    public void DeletionAndGlobalLimitTakeEffect()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SData:Rum:CorsOrigins:0"] = "https://other.example.com",
        }).Build();
        Assert.False(new RumOriginPolicy(_services.GetRequiredService<IServiceScopeFactory>(), configuration).Allows("rum_demo", "https://app.example.com"));
        Assert.True(_policy.AllowsAny("https://app.example.com"));
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        db.RumProjects.Remove(db.RumProjects.Single(x => x.ProjectKey == "rum_demo"));
        db.SaveChanges();
        Assert.False(_policy.Allows("rum_demo", "https://app.example.com"));
        Assert.False(_policy.AllowsAny("https://app.example.com"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("[\"example.com\"]")]
    [InlineData("[\"https://example.com/path\"]")]
    [InlineData("[\"https://*.example.com\"]")]
    [InlineData("[\"https://user:pass@example.com\"]")]
    public void InvalidConfigurationDenied(string json) => Assert.False(RumOrigins.TryParse(json, out _));

    [Fact]
    public async Task DeniedBeaconNeverEnqueuesAndConfigUsesSavedRates()
    {
        var buffer = new IngestBuffer(new SystemOptions());
        var service = new RumBeaconService(buffer, scopeFactory: _services.GetRequiredService<IServiceScopeFactory>(), originPolicy: _policy);
        var body = Encoding.UTF8.GetBytes("""{"projectKey":"rum_demo","sessionId":"test","events":[{"type":"click"}]}""");
        Assert.Equal(RumIngestStatus.Forbidden, await service.IngestAsync(body, null, "https://evil.test"));
        Assert.False(buffer.Reader.TryRead(out _));
        Assert.Null(service.GetConfig("rum_demo", "https://evil.test"));
        var config = service.GetConfig("rum_demo", "https://app.example.com");
        Assert.NotNull(config);
        Assert.Equal(0.5, config.PerfSampleRate);
        Assert.Equal(0.2, config.ReplaySampleRate);
        Assert.Equal(RumIngestStatus.Ok, await service.IngestAsync(body, null, "https://app.example.com"));
        Assert.True(buffer.Reader.TryRead(out _));
    }

    [Fact]
    public void OriginHeaderCannotFallBackWhenInvalid()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Referer = "https://app.example.com/page";
        Assert.Equal("https://app.example.com", RumRequestOrigin.Read(request));
        request.Headers.Origin = "null";
        Assert.Null(RumRequestOrigin.Read(request));
        request.Headers.Origin = new Microsoft.Extensions.Primitives.StringValues(["https://app.example.com", "https://evil.test"]);
        Assert.Null(RumRequestOrigin.Read(request));
        request.Headers.Remove("Origin");
        request.Headers.Remove("Referer");
        Assert.Null(RumRequestOrigin.Read(request));
    }

    public void Dispose()
    {
        _services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }
}
