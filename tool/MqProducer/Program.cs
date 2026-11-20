using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using DotPulsar;
using DotPulsar.Extensions;
using RabbitMQ.Client;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
var runId = Environment.GetEnvironmentVariable("RUN_ID") ?? Guid.NewGuid().ToString("N");
var host = Environment.GetEnvironmentVariable("MQ_HOST") ?? "localhost";
var count = int.TryParse(Environment.GetEnvironmentVariable("COUNT"), out var total) ? total : 0;
var interval = int.TryParse(Environment.GetEnvironmentVariable("INTERVAL_MS"), out var ms) ? Math.Max(ms, 100) : 1000;
var target = Environment.GetEnvironmentVariable("TARGET") ?? "all";
var suffix = Environment.GetEnvironmentVariable("TOPIC_SUFFIX") ?? "exact";
var name = "sdata-e2e-" + suffix;
var tasks = new[] { "kafka", "rabbitmq", "pulsar" }.Where(type => target == "all" || target == type)
    .Select(type => RunAsync(type, stop.Token));
try { await Task.WhenAll(tasks); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

async Task RunAsync(string type, CancellationToken token)
{
    long sequence = 0;
    while (!token.IsCancellationRequested && (count == 0 || sequence < count))
    {
        try
        {
            using var kafka = type == "kafka" ? new ProducerBuilder<Null, byte[]>(new ProducerConfig
            {
                BootstrapServers = host + ":9092",
                EnableIdempotence = true,
                SecurityProtocol = Enum.Parse<SecurityProtocol>(Environment.GetEnvironmentVariable("KAFKA_SECURITY_PROTOCOL") ?? "Plaintext"),
                SaslMechanism = Enum.Parse<SaslMechanism>(Environment.GetEnvironmentVariable("KAFKA_SASL_MECHANISM") ?? "ScramSha256"),
                SaslUsername = Environment.GetEnvironmentVariable("KAFKA_SASL_USERNAME"),
                SaslPassword = Environment.GetEnvironmentVariable("KAFKA_SASL_PASSWORD"),
                MessageTimeoutMs = 10000,
            }).Build() : null;
            await using var rabbit = type == "rabbitmq" ? await new ConnectionFactory
            {
                Uri = new Uri(Environment.GetEnvironmentVariable("RABBIT_URI") ?? throw new InvalidOperationException("请通过 RABBIT_URI 环境变量提供 RabbitMQ 连接信息")),
            }.CreateConnectionAsync(token) : null;
            await using var channel = rabbit == null ? null : await rabbit.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), token);
            if (channel != null)
            {
                await channel.QueueDeclareAsync(name, true, false, false, new Dictionary<string, object?>
                {
                    ["x-max-length"] = 10000, ["x-message-ttl"] = 3600000,
                }, cancellationToken: token);
            }

            await using var pulsar = type == "pulsar" ? PulsarClient.Builder().ServiceUrl(new Uri($"pulsar://{host}:6650")).Build() : null;
            await using var producer = pulsar?.NewProducer(Schema.ByteArray).Topic("persistent://public/default/" + name).Create();
            while (!token.IsCancellationRequested && (count == 0 || sequence < count))
            {
                var next = sequence + 1;
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    timestamp = DateTimeOffset.UtcNow, service = "mq-e2e-" + type, severity = "INFO",
                    body = $"sdata-mq-e2e run={runId} broker={type} topic={name} seq={next}",
                    attributes = new Dictionary<string, string> { ["runId"] = runId, ["sequence"] = next.ToString(), ["broker"] = type },
                });
                if (kafka != null) await kafka.ProduceAsync(name, new Message<Null, byte[]> { Value = payload }, token);
                if (channel != null) await channel.BasicPublishAsync("", name, true, new BasicProperties { Persistent = true }, payload, token);
                if (producer != null) await producer.Send(payload, token);
                sequence = next;
                Console.WriteLine($"{DateTimeOffset.UtcNow:O} {type} run={runId} topic={name} seq={sequence}");
                await Task.Delay(interval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{type}: {exception.GetType().Name}; retrying in 5s");
            await Task.Delay(5000, token);
        }
    }
}
