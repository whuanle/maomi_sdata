using FluentValidation;
using SData.Infra.Configuration;
using SData.Ingest;
using SData.Monitor.Handlers;
using SData.Settings;
using SData.Store.Models;
using Xunit;

namespace SData.Monitor.Tests;

public class MqConsumptionTests
{
    [Fact]
    public async Task QueryFiltersDeletedInstancesAndNeverRestoresHistoricalState()
    {
        var id = Random.Shared.NextInt64(1_000_000, long.MaxValue - 1);
        MqRuntime.Start(id, [("logs", "exact")]);
        MqRuntime.Connected(id);
        try
        {
            var handler = new QueryMqConsumptionCommandHandler(new Settings(id), new History(id));
            var now = DateTimeOffset.UtcNow;
            var response = await handler.Handle(new QueryMqConsumptionCommand { From = now.AddHours(-1), To = now }, CancellationToken.None);
            Assert.Equal(2, response.Instances.Count);
            Assert.Equal("idle", response.Instances[0].State);
            Assert.Equal("disabled", response.Instances[1].State);
            Assert.Single(response.History);
            Assert.Equal("old-session", response.History[0].SessionId);
            Assert.NotEqual("old-session", response.Instances[0].SessionId);
        }
        finally
        {
            MqRuntime.Remove(id);
        }
    }

    [Fact]
    public void HistoryRequiresPairedBoundedRange()
    {
        var validator = new InlineValidator<QueryMqConsumptionCommand>();
        QueryMqConsumptionCommand.Validate(validator);
        var now = DateTimeOffset.UtcNow;
        Assert.True(validator.Validate(new QueryMqConsumptionCommand()).IsValid);
        Assert.False(validator.Validate(new QueryMqConsumptionCommand { From = now }).IsValid);
        Assert.False(validator.Validate(new QueryMqConsumptionCommand { From = now.AddDays(-8), To = now }).IsValid);
        Assert.False(validator.Validate(new QueryMqConsumptionCommand { From = now, To = now }).IsValid);
    }

    private sealed class History(long id) : IMqConsumptionStore
    {
        public Task<IReadOnlyList<MqConsumptionRecord>> QueryAsync(DateTimeOffset from, DateTimeOffset to, long? instanceId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MqConsumptionRecord>>([
                new() { InstanceId = id, SessionId = "old-session", State = "consuming" },
                new() { InstanceId = id + 100, SessionId = "deleted" },
            ]);
    }

    private sealed class Settings(long id) : ISystemSettingsProvider
    {
        public List<MqInstanceOptions> GetMqInstances() => [
            new() { Id = id, Enabled = true, Topics = [new() { Name = "logs" }] },
            new() { Id = id + 1, Enabled = false },
        ];
        public S3Options GetS3() => new();
        public RetentionPlanOptions GetRetention() => new();
        public CompressionOptions GetCompression() => new();
        public OtlpOptions GetOtlp() => new();
        public Task SaveAsync(string key, string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
