using System.Text;
using Confluent.Kafka;

namespace Plaza.Testing;

/// <summary>A message read back from a topic by a test.</summary>
public sealed record ReceivedMessage(string Key, string Value, IReadOnlyDictionary<string, string> Headers);

/// <summary>Raw Kafka access for tests: write arbitrary messages and read topics back.</summary>
public static class KafkaTestClient
{
    public static async Task ProduceAsync(
        string bootstrapServers, string topic, string key, string value, IReadOnlyDictionary<string, string> headers)
    {
        using var producer = new ProducerBuilder<string, byte[]>(
            new ProducerConfig { BootstrapServers = bootstrapServers, Acks = Acks.All }).Build();

        var kafkaHeaders = new Headers();
        foreach (var (name, headerValue) in headers)
        {
            kafkaHeaders.Add(name, Encoding.UTF8.GetBytes(headerValue));
        }

        await producer.ProduceAsync(topic, new Message<string, byte[]>
        {
            Key = key,
            Value = Encoding.UTF8.GetBytes(value),
            Headers = kafkaHeaders,
        });
    }

    /// <summary>
    /// Reads the topic from the beginning until <paramref name="until"/> is satisfied.
    /// Throws <see cref="TimeoutException"/> with what was read so far otherwise.
    /// </summary>
    public static Task<IReadOnlyList<ReceivedMessage>> ReadUntilAsync(
        string bootstrapServers, string topic, Func<IReadOnlyList<ReceivedMessage>, bool> until, TimeSpan timeout) =>
        Task.Run<IReadOnlyList<ReceivedMessage>>(() =>
        {
            using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = bootstrapServers,
                GroupId = $"test-reader-{Guid.NewGuid():N}",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
            }).Build();
            consumer.Subscribe(topic);

            var received = new List<ReceivedMessage>();
            var deadline = DateTime.UtcNow + timeout;
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    ConsumeResult<string, byte[]>? result;
                    try
                    {
                        result = consumer.Consume(TimeSpan.FromMilliseconds(200));
                    }
                    catch (ConsumeException)
                    {
                        // Topic not created yet.
                        continue;
                    }

                    if (result?.Message is null)
                    {
                        continue;
                    }

                    var headers = new Dictionary<string, string>();
                    foreach (var header in result.Message.Headers)
                    {
                        headers[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes());
                    }

                    received.Add(new ReceivedMessage(
                        result.Message.Key, Encoding.UTF8.GetString(result.Message.Value), headers));

                    if (until(received))
                    {
                        return received;
                    }
                }
            }
            finally
            {
                consumer.Close();
            }

            throw new TimeoutException(
                $"Condition not met on '{topic}' within {timeout}. Read {received.Count} message(s): " +
                string.Join(" | ", received.Select(m => m.Value)));
        });
}
