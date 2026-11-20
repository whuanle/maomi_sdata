using global::Xunit;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Infra.Configuration;
using SData.Settings;
using SData.Store.Core;

namespace SData.Store.Tests;

public class StorageMetricsTests
{
    [Fact]
    public void DiskMatching_UsesLongestMountBoundary()
    {
        Assert.Equal("/data", StorageMetricsService.FindVolume("/data/db/main.duckdb", ["/", "/data", "/database"]));
        Assert.Equal("/", StorageMetricsService.FindVolume("/data2/main.duckdb", ["/", "/data"]));
    }

    [Fact]
    public async Task S3Listing_CollectsEveryPageWithPrefixBoundary()
    {
        using var client = new FakeS3();
        var result = await StorageMetricsService.ListSizeAsync(client, "bucket", "archive", CancellationToken.None);
        Assert.Equal(30, result.Bytes);
        Assert.Equal(2, result.Objects);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task S3Listing_PartialFailureDoesNotReturnPartialSize()
    {
        using var client = new FakeS3 { FailSecond = true };
        await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            StorageMetricsService.ListSizeAsync(client, "bucket", "archive", CancellationToken.None));
    }

    [Fact]
    public async Task DuckDbMetrics_ReadActualMemorySettingsAndFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdata-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new SystemOptions
        {
            DuckDb = new DuckDbOptions { Path = Path.Combine(directory, "metrics.duckdb"), MemoryLimit = "128MB", Threads = 2 },
        };
        var manager = new DuckDbConnectionManager(options, NullLogger<DuckDbConnectionManager>.Instance);
        try
        {
            await manager.InitializeAsync();
            using var sampler = new StorageMetricsService(manager, options, new Settings(), NullLogger<StorageMetricsService>.Instance);
            var snapshot = await sampler.SampleLocalAsync(CancellationToken.None);
            Assert.Equal("ok", snapshot.DuckDbState);
            Assert.Equal("ok", snapshot.FileState);
            Assert.True(snapshot.TrackedMemoryBytes >= 0);
            Assert.True(snapshot.TemporaryBytes >= 0);
            Assert.True(snapshot.DatabaseBytes > 0);
            Assert.Equal(2, snapshot.Threads);
            Assert.NotEmpty(snapshot.MemoryLimit!);
            Assert.NotEmpty(snapshot.Disks);
        }
        finally
        {
            await manager.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false, "forbidden")]
    [InlineData(true, "timeout")]
    public async Task S3Sampling_ClassifiesFailuresWithoutLeakingErrors(bool timeout, string expected)
    {
        var settings = new Settings { Target = new S3Options { Endpoint = "localhost", Bucket = "archive", SecretKey = "secret" } };
        var sampler = new S3MetricsSampler(settings, _ => new FailingS3(timeout), TimeSpan.FromMilliseconds(20));
        await sampler.SampleIfDueAsync(CancellationToken.None);
        Assert.Equal(expected, sampler.Snapshot.State);
        Assert.Null(sampler.Snapshot.Bytes);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(sampler.Snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public async Task S3Sampling_ConfigChangeInvalidatesOldTargetImmediately()
    {
        var settings = new Settings { Target = new S3Options { Endpoint = "localhost", Bucket = "first", Prefix = "archive" } };
        var sampler = new S3MetricsSampler(settings, _ => new FakeS3());
        await sampler.SampleIfDueAsync(CancellationToken.None);
        Assert.Equal(30, sampler.Snapshot.Bytes);
        settings.Target = new S3Options { Endpoint = "localhost", Bucket = "second", Prefix = "other" };
        Assert.Null(sampler.Snapshot.Bytes);
        Assert.Equal("second", sampler.Snapshot.Bucket);
        Assert.Equal("unknown", sampler.Snapshot.State);
    }

    private sealed class FailingS3(bool timeout) : AmazonS3Client("test", "test", new AmazonS3Config { ServiceURL = "http://localhost" })
    {
        public override async Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken = default)
        {
            if (timeout)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            throw new AmazonS3Exception("secret") { StatusCode = System.Net.HttpStatusCode.Forbidden };
        }
    }

    private sealed class FakeS3() : AmazonS3Client("test", "test", new AmazonS3Config { ServiceURL = "http://localhost" })
    {
        public int Calls { get; private set; }
        public bool FailSecond { get; init; }

        public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Equal("archive/", request.Prefix);
            if (Calls == 2 && FailSecond) throw new AmazonS3Exception("failure");
            return Task.FromResult(new ListObjectsV2Response
            {
                S3Objects = [new S3Object { Key = "archive/item", Size = Calls * 10 }],
                IsTruncated = Calls == 1,
                NextContinuationToken = Calls == 1 ? "next" : null,
            });
        }
    }

    private sealed class Settings : ISystemSettingsProvider
    {
        public S3Options Target { get; set; } = new();
        public S3Options GetS3() => Target;
        public List<MqInstanceOptions> GetMqInstances() => [];
        public RetentionPlanOptions GetRetention() => new();
        public CompressionOptions GetCompression() => new();
        public OtlpOptions GetOtlp() => new();
        public Task SaveAsync(string key, string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
