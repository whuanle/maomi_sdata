using System.IdentityModel.Tokens.Jwt;
using System.Net;
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
using SData.Infra.Api;
using SData.Infra.Api.Conventions;
using SData.Infra.Configuration;
using SData.Infra.Services;
using SData.Settings;
using SData.Settings.Api;
using SData.Settings.Handlers;
using SData.Settings.Services;
using SData.Store;
using SData.Store.Core;
using Xunit;

namespace SData.Ingest.Tests;

public class DuckDbSettingsIntegrationTests
{
    [Fact]
    public async Task MemorySettings_AdminOnly_ApplyAndSurviveRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdata-memory-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new SystemOptions();
        options.Database.Path = Path.Combine(directory, "settings.db");
        options.DuckDb.Path = Path.Combine(directory, "data.duckdb");
        var key = new SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        try
        {
            await using (var app = Build(options, key))
            {
                await PrepareDatabaseAsync(app);
                await app.StartAsync();
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var client = new HttpClient { BaseAddress = new Uri(address) };
                var payload = new { memoryLimit = "2GB" };
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/settings/duckdb", payload)).StatusCode);
                foreach (var role in new[] { "readonly", "write" })
                {
                    client.DefaultRequestHeaders.Authorization = new("Bearer", Token(role, key));
                    Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/settings/duckdb", payload)).StatusCode);
                }

                client.DefaultRequestHeaders.Authorization = new("Bearer", Token("admin", key));
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings/duckdb", new { memoryLimit = "unlimited" })).StatusCode);
                var saved = await client.PutAsJsonAsync("/api/settings/duckdb", payload);
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
                var result = (await saved.Content.ReadFromJsonAsync<DuckDbSettingsResponse>())!;
                Assert.Equal("2GB", result.MemoryLimit);
                Assert.Equal(app.Services.GetRequiredService<IDuckDbRuntimeSettings>().EffectiveMemoryLimit, result.EffectiveMemoryLimit);
                var queried = (await client.GetFromJsonAsync<SettingsResponse>("/api/settings"))!;
                Assert.Equal(result.EffectiveMemoryLimit, queried.DuckDb.EffectiveMemoryLimit);
                Assert.Equal("2GB", queried.DuckDb.MemoryLimit);
                await app.StopAsync();
            }

            await using (var restarted = Build(options, key))
            {
                await restarted.StartAsync();
                Assert.Equal("2GB", restarted.Services.GetRequiredService<IDuckDbRuntimeSettings>().MemoryLimit);
                Assert.Equal("512MB", options.DuckDb.MemoryLimit);
                await restarted.StopAsync();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static WebApplication Build(SystemOptions options, SecurityKey key)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(options);
        builder.Services.AddDbContext<DatabaseContext>(o => o.UseSqlite("Data Source=" + options.Database.Path));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IUserContext, UserContext>();
        builder.Services.AddScoped<IUserContextProvider, DefaultUserContextProvider>();
        builder.Services.AddSingleton<IIdProvider>(new DefaultIdProvider(1));
        builder.Services.AddSingleton<ISystemSettingsProvider, SystemSettingsProvider>();
        builder.Services.AddSingleton<DuckDbSettingsService>();
        builder.Services.AddSingleton<DuckDbConnectionManager>();
        builder.Services.AddSingleton<IDuckDbRuntimeSettings>(sp => sp.GetRequiredService<DuckDbConnectionManager>());
        builder.Services.AddSingleton<DuckDbSchemaBootstrapper>();
        builder.Services.AddSingleton<IS3SecretManager, S3SecretManager>();
        builder.Services.AddHostedService<StoreInitializer>();
        builder.Services.AddMediatR(o => o.RegisterServicesFromAssembly(typeof(SaveDuckDbSettingsCommandHandler).Assembly));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true,
                IssuerSigningKey = key, ValidateIssuerSigningKey = true,
            });
        builder.Services.AddAuthorization();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<BusinessExceptionHandler>();
        builder.Services.AddControllers(o => o.Conventions.Add(new ApiRouteConvention("api")))
            .AddApplicationPart(typeof(SettingsController).Assembly);
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        return app;
    }

    private static async Task PrepareDatabaseAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseContext>().Database.EnsureCreatedAsync();
    }

    private static string Token(string role, SecurityKey key)
        => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim(ClaimTypes.Role, role), new Claim("userid", "1")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
}
