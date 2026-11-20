using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using SData.Settings.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Agents;
using SData.Database;
using SData.Infra.Services;
using SData.Ingest.Services;
using SData.Settings;
using SData.Settings.Handlers;
using SData.Store.Models;
using Xunit;

namespace SData.Store.Tests;

public class ApiAgentTests
{
    [Fact]
    public async Task Keys_AreRecoverable_AndRevocationInvalidatesCachedValidation()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var services = new ServiceCollection();
        services.AddDbContext<DatabaseContext>(o => o.UseSqlite("Data Source=" + file));
        await using var provider = services.BuildServiceProvider();
        try
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            await db.Database.EnsureCreatedAsync();
            var protection = new ApiKeyProtection(new EphemeralDataProtectionProvider());
            var handler = new ManageApiKeyCommandHandler(db, new TestIds(), NullLogger<ManageApiKeyCommandHandler>.Instance, protection);
            var validator = new IngestKeyValidator(provider.GetRequiredService<IServiceScopeFactory>());
            var created = await handler.Handle(new() { Kind = "ingestion", Action = "create", Name = "collector" }, default);
            Assert.Equal(68, created.Secret.Length);
            Assert.True(await validator.ValidateAsync(created.Secret));
            var agent = await handler.Handle(new() { Kind = "agent", Action = "create", Name = "agent" }, default);
            Assert.False(await validator.ValidateAsync(agent.Secret));
            foreach (var credential in new[] { created, agent })
            {
                Assert.True(credential.Item.CanReveal);
                var before = credential.Item.UpdateTime;
                var revealed = await handler.Handle(new() { Kind = credential.Item.Kind, Id = credential.Item.Id, Action = "reveal" }, default);
                Assert.Equal(credential.Secret, revealed.Secret);
                Assert.Equal(before, revealed.Item.UpdateTime);
            }
            var ciphertext = (await db.IngestKeys.SingleAsync()).ProtectedSecret;
            Assert.DoesNotContain(created.Secret, ciphertext);
            var listing = JsonSerializer.Serialize(await new QueryApiKeysCommandHandler(db).Handle(new(), default));
            Assert.DoesNotContain(created.Secret, listing);
            Assert.DoesNotContain(ciphertext, listing);
            Assert.DoesNotContain(HashHelper.Sha256Hex(created.Secret), listing);
            var rotated = await handler.Handle(new() { Kind = "ingestion", Id = created.Item.Id, Action = "rotate" }, default);
            Assert.False(await validator.ValidateAsync(created.Secret));
            Assert.True(await validator.ValidateAsync(rotated.Secret));
            Assert.Equal(rotated.Secret, (await handler.Handle(new() { Kind = "ingestion", Id = created.Item.Id, Action = "reveal" }, default)).Secret);
            var rotatedAgent = await handler.Handle(new() { Kind = "agent", Id = agent.Item.Id, Action = "rotate" }, default);
            Assert.NotEqual(agent.Secret, rotatedAgent.Secret);
            Assert.Equal(rotatedAgent.Secret, (await handler.Handle(new() { Kind = "agent", Id = agent.Item.Id, Action = "reveal" }, default)).Secret);
            await handler.Handle(new() { Kind = "ingestion", Id = created.Item.Id, Action = "enable", Enabled = false }, default);
            Assert.False(await validator.ValidateAsync(rotated.Secret));
            await handler.Handle(new() { Kind = "agent", Id = agent.Item.Id, Action = "delete" }, default);
            Assert.Empty(await db.AgentKeys.ToListAsync());
            Assert.Single(await db.AgentKeys.IgnoreQueryFilters().ToListAsync());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(file); }
    }

    [Fact]
    public async Task Tools_UseBoundParameters_AllowlistAndBudgets()
    {
        var executor = new RecordingExecutor();
        var queries = new AgentQueries(executor, NullLogger<AgentQueries>.Instance);
        const string hostile = "'; DROP TABLE user;--";
        var response = await queries.ExecuteAsync("search_logs", new() { Service = hostile }, default);
        Assert.DoesNotContain(hostile, executor.Sql);
        Assert.Equal(hostile, executor.Parameters!["service"]);
        Assert.Contains("[REDACTED]", JsonSerializer.Serialize(response));
        foreach (var tool in new[] { "list_services", "get_data_schema", "search_traces", "get_trace", "list_metric_names", "query_metric_series", "summarize_errors" })
            await queries.ExecuteAsync(tool, new() { TraceId = "trace", Metric = "cpu" }, default);
        await Assert.ThrowsAsync<SData.Infra.Exceptions.BusinessException>(() => queries.ExecuteAsync("execute_sql", new(), default));
        await Assert.ThrowsAsync<SData.Infra.Exceptions.BusinessException>(() => queries.ExecuteAsync("search_logs", new() { Limit = 201 }, default));
        await Assert.ThrowsAsync<SData.Infra.Exceptions.BusinessException>(() => queries.ExecuteAsync("search_logs", new() { From = DateTimeOffset.UtcNow.AddDays(-2) }, default));
    }

    [Fact]
    public void Protection_PersistsAndRejectsWrongPurposeOrMaterial()
    {
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "api-key-ring-" + Guid.NewGuid()));
        directory.Create();
        try
        {
            var first = new ApiKeyProtection(DataProtectionProvider.Create(directory, options => options.SetApplicationName("SData")));
            var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var hash = HashHelper.Sha256Hex(secret);
            var encrypted = first.Protect("ingestion", 100, secret);
            var restarted = new ApiKeyProtection(DataProtectionProvider.Create(directory, options => options.SetApplicationName("SData")));
            Assert.Equal(secret, restarted.Reveal("ingestion", 100, encrypted, hash));
            foreach (var action in new Action[]
            {
                () => restarted.Reveal("agent", 100, encrypted, hash),
                () => restarted.Reveal("ingestion", 101, encrypted, hash),
                () => restarted.Reveal("ingestion", 100, encrypted, HashHelper.Sha256Hex("different")),
                () => restarted.Reveal("ingestion", 100, "damaged", hash),
                () => restarted.Reveal("ingestion", 100, "", hash),
                () => new ApiKeyProtection(new EphemeralDataProtectionProvider()).Reveal("ingestion", 100, encrypted, hash),
            })
            {
                var error = Assert.Throws<SData.Infra.Exceptions.BusinessException>(action);
                Assert.Equal(409, error.StatusCode);
                Assert.DoesNotContain(secret, error.Message);
                Assert.DoesNotContain(encrypted, error.Message);
            }
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task LegacyKey_RemainsValidUntilExplicitReset()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        await using var db = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        db.IngestKeys.Add(new SData.Database.Entities.IngestKeyEntity { Id = 100, Name = "legacy", KeyHash = HashHelper.Sha256Hex(secret) });
        await db.SaveChangesAsync();
        var entity = await db.IngestKeys.SingleAsync();
        var before = entity.UpdateTime;
        var handler = new ManageApiKeyCommandHandler(db, new TestIds(), NullLogger<ManageApiKeyCommandHandler>.Instance,
            new ApiKeyProtection(new EphemeralDataProtectionProvider()));
        Assert.False((await new QueryApiKeysCommandHandler(db).Handle(new(), default)).Single().CanReveal);
        await Assert.ThrowsAsync<SData.Infra.Exceptions.BusinessException>(() => handler.Handle(new() { Kind = "ingestion", Id = "100", Action = "reveal" }, default));
        Assert.True(entity.Enabled);
        Assert.Equal(HashHelper.Sha256Hex(secret), entity.KeyHash);
        Assert.Equal(before, entity.UpdateTime);
        var reset = await handler.Handle(new() { Kind = "ingestion", Id = "100", Action = "rotate" }, default);
        Assert.True(reset.Item.CanReveal);
        Assert.NotEqual(HashHelper.Sha256Hex(secret), entity.KeyHash);
    }

    private class TestIds : IIdProvider
    {
        private long _id = 100;
        public long GetLongId() => Interlocked.Increment(ref _id);
    }

    private class RecordingExecutor : IAnalyticsExecutor
    {
        public string Sql = "";
        public IReadOnlyDictionary<string, object?>? Parameters;
        public Task<AnalyticsQueryResult> ExecuteAsync(string sql, int rowLimit = 1000, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AnalyticsQueryResult> ExecuteSearchAsync(string sql, DateTimeOffset from, DateTimeOffset to, int rowLimit = 500, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
        {
            Sql = sql; Parameters = parameters;
            return Task.FromResult(new AnalyticsQueryResult { Columns = ["body"], Rows = [["password=secret-value"]] });
        }
    }
}
