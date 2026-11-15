using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Plaza.Messaging.Kafka;

/// <summary>A message ready to be written to Kafka.</summary>
public sealed record OutgoingMessage(
    string Topic,
    string Key,
    byte[] Value,
    IReadOnlyList<KeyValuePair<string, byte[]>> Headers);

/// <summary>Writes messages to the broker. The seam used by tests to inject publish failures.</summary>
public interface IMessageProducer
{
    /// <summary>
    /// Produces the messages in order and completes when the broker has acknowledged all of
    /// them. Throws if any message could not be delivered.
    /// </summary>
    Task ProduceAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken);
}

internal sealed class KafkaMessageProducer : IMessageProducer, IDisposable
{
    private readonly Lazy<IProducer<string, byte[]>> _producer;

    public KafkaMessageProducer(IOptions<MessagingOptions> options)
    {
        _producer = new Lazy<IProducer<string, byte[]>>(() =>
            new ProducerBuilder<string, byte[]>(new ProducerConfig
            {
                BootstrapServers = options.Value.BootstrapServers,
                ClientId = options.Value.ServiceName,
                // Idempotent producer: broker-side deduplication of client retries, and
                // per-partition ordering is kept even when a batch is retried.
                EnableIdempotence = true,
                Acks = Acks.All,
                LingerMs = 5,
                // Surface a broker outage as an error instead of buffering forever.
                MessageTimeoutMs = 30_000,
            }).Build());
    }

    public async Task ProduceAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken)
    {
        var producer = _producer.Value;

        // ProduceAsync enqueues synchronously, so calling it in a loop preserves order;
        // the tasks complete when the broker acknowledges each message.
        var deliveries = new List<Task>(messages.Count);
        foreach (var message in messages)
        {
            var headers = new Headers();
            foreach (var (name, value) in message.Headers)
            {
                headers.Add(name, value);
            }

            deliveries.Add(producer.ProduceAsync(
                message.Topic,
                new Message<string, byte[]> { Key = message.Key, Value = message.Value, Headers = headers },
                cancellationToken));
        }

        await Task.WhenAll(deliveries);
    }

    public void Dispose()
    {
        if (_producer.IsValueCreated)
        {
            _producer.Value.Flush(TimeSpan.FromSeconds(5));
            _producer.Value.Dispose();
        }
    }
}
