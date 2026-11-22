using System.IdentityModel.Tokens.Jwt;
using System.Net;
using Mediator;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Api;
using SData.Infra.Api.Conventions;
using SData.Infra.Services;
using SData.Settings;
using SData.Settings.Api;
using SData.Settings.Handlers;
using SData.Settings.Services;
using Xunit;

namespace SData.Ingest.Tests;

public class ApiKeyManagementIntegrationTests
{
    [Fact]
    public async Task RevealAndReset_RequireAdmin_NeverCacheSecrets()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var signingKey = new SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<DatabaseContext>(o => o.UseSqlite("Data Source=" + file));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IUserContext, UserContext>();
        builder.Services.AddScoped<IUserContextProvider, DefaultUserContextProvider>();
        builder.Services.AddSingleton<IIdProvider>(new DefaultIdProvider(1));
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<ApiKeyProtection>();
        builder.Services.AddMediator();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true,
                IssuerSigningKey = signingKey, ValidateIssuerSigningKey = true,
            });
        builder.Services.AddAuthorization();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<BusinessExceptionHandler>();
        builder.Services.AddControllers(o => o.Conventions.Add(new ApiRouteConvention("api")))
            .AddApplicationPart(typeof(SettingsController).Assembly);
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            await db.Database.EnsureCreatedAsync();
            db.IngestKeys.Add(new IngestKeyEntity { Id = 99, Name = "legacy", KeyHash = HashHelper.Sha256Hex(Guid.NewGuid().ToString()) });
            await db.SaveChangesAsync();
        }
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var reveal = new ManageApiKeyCommand { Kind = "ingestion", Action = "reveal", Id = "99" };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/settings/api-keys", reveal)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token("readonly", signingKey));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/settings/api-keys", reveal)).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", signingKey));
            var legacy = await client.PostAsJsonAsync("/api/settings/api-keys", reveal);
            Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
            Assert.True(legacy.Headers.CacheControl?.NoStore);
            foreach (var kind in new[] { "ingestion", "agent" })
            {
                var created = await client.PostAsJsonAsync("/api/settings/api-keys", new ManageApiKeyCommand { Kind = kind, Action = "create", Name = kind });
                Assert.Equal(HttpStatusCode.OK, created.StatusCode);
                Assert.True(created.Headers.CacheControl?.NoStore);
                var first = (await created.Content.ReadFromJsonAsync<ApiKeySecretResponse>())!;
                Assert.True(first.Item.CanReveal);
                Assert.Equal("1", first.Item.CreateUserId);
                reveal.Kind = kind;
                reveal.Id = first.Item.Id;
                var viewed = await client.PostAsJsonAsync("/api/settings/api-keys", reveal);
                Assert.Equal(HttpStatusCode.OK, viewed.StatusCode);
                Assert.True(viewed.Headers.CacheControl?.NoStore);
                var second = (await viewed.Content.ReadFromJsonAsync<ApiKeySecretResponse>())!;
                Assert.Equal(first.Secret, second.Secret);
                Assert.Equal(first.Item.UpdateTime, second.Item.UpdateTime);
                var reset = await client.PostAsJsonAsync("/api/settings/api-keys", new ManageApiKeyCommand { Kind = kind, Action = "rotate", Id = first.Item.Id });
                var next = (await reset.Content.ReadFromJsonAsync<ApiKeySecretResponse>())!;
                Assert.NotEqual(first.Secret, next.Secret);
                var viewedAgain = await client.PostAsJsonAsync("/api/settings/api-keys", reveal);
                Assert.Equal(next.Secret, (await viewedAgain.Content.ReadFromJsonAsync<ApiKeySecretResponse>())!.Secret);
                var listing = await client.GetAsync("/api/settings/api-keys");
                Assert.True(listing.Headers.CacheControl?.NoStore);
                var body = await listing.Content.ReadAsStringAsync();
                Assert.DoesNotContain(first.Secret, body);
                Assert.DoesNotContain(next.Secret, body);
            }
        }
        finally
        {
            await app.StopAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    private static string Token(string role, SecurityKey key)
        => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim(ClaimTypes.Role, role), new Claim("userid", "1")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
}
