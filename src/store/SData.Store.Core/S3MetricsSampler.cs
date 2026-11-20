using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using SData.Infra.Configuration;
using SData.Settings;

namespace SData.Store;

/// <summary>有界只读列举；配置切换立即使旧目标快照失效.</summary>
public sealed class S3MetricsSampler(
    ISystemSettingsProvider settings,
    Func<S3Options, IAmazonS3>? createClient = null,
    TimeSpan? timeout = null)
{
    private sealed record Published(string Key, S3StorageSnapshot Snapshot);

    private Published _published = new(string.Empty, new());
    private int _sampling;

    public S3StorageSnapshot Snapshot
    {
        get
        {
            var target = settings.GetS3();
            var published = Volatile.Read(ref _published);
            return published.Key == Fingerprint(target) ? published.Snapshot : Empty(target);
        }
    }

    public async Task SampleIfDueAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _sampling, 1) != 0)
        {
            return;
        }

        try
        {
            var target = settings.GetS3();
            var key = Fingerprint(target);
            var published = Volatile.Read(ref _published);
            var previous = published.Key == key ? published.Snapshot : Empty(target);
            if (previous.State == "notConfigured")
            {
                Volatile.Write(ref _published, new Published(key, previous));
                return;
            }

            var interval = TimeSpan.FromMinutes(previous.State == "ok" ? 10 : 1);
            if (previous.CheckedAt.HasValue && DateTimeOffset.UtcNow - previous.CheckedAt.Value < interval)
            {
                return;
            }

            Volatile.Write(ref _published, new Published(key, previous with { State = "checking" }));
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            S3StorageSnapshot result;
            try
            {
                using var client = (createClient ?? CreateClient)(target);
                var totals = await StorageMetricsService.ListSizeAsync(client, target.Bucket, target.Prefix, budget.Token);
                result = previous with
                {
                    State = "ok", Bytes = totals.Bytes, Objects = totals.Objects, SampledAt = DateTimeOffset.UtcNow,
                };
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                result = previous with { State = "timeout" };
            }
            catch (AmazonS3Exception exception)
            {
                result = previous with { State = exception.StatusCode == HttpStatusCode.Forbidden ? "forbidden" : "error" };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 不传播 SDK 异常正文，其中可能含签名请求或凭据。
                result = previous with { State = "error" };
            }

            var current = settings.GetS3();
            Volatile.Write(ref _published, Fingerprint(current) == key
                ? new Published(key, result with { CheckedAt = DateTimeOffset.UtcNow })
                : new Published(Fingerprint(current), Empty(current)));
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }

    private static bool Configured(S3Options target)
        => !string.IsNullOrWhiteSpace(target.Endpoint) && !string.IsNullOrWhiteSpace(target.Bucket);

    private static S3StorageSnapshot Empty(S3Options target)
        => new()
        {
            State = Configured(target) ? "unknown" : "notConfigured",
            Bucket = target.Bucket,
            Prefix = string.IsNullOrWhiteSpace(target.Prefix) ? string.Empty : target.Prefix.Trim('/') + "/",
        };

    private static string Fingerprint(S3Options target)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(target))));

    private static IAmazonS3 CreateClient(S3Options target)
        => new AmazonS3Client(target.AccessKey, target.SecretKey, new AmazonS3Config
        {
            ServiceURL = target.Endpoint.Contains("://", StringComparison.Ordinal) ? target.Endpoint : "http://" + target.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            Timeout = TimeSpan.FromSeconds(15),
            MaxErrorRetry = 0,
        });
}
