namespace Plaza.Messaging;

public sealed class MessagingOptions
{
    /// <summary>Kafka bootstrap servers. Defaults to <c>ConnectionStrings:kafka</c>.</summary>
    public string BootstrapServers { get; set; } = "";

    /// <summary>Consumer group id and inbox consumer name. One per service.</summary>
    public string ServiceName { get; set; } = "";

    /// <summary>Total handler attempts before a message is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 5;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    public int OutboxBatchSize { get; set; } = 100;

    /// <summary>
    /// Partitions for topics created at startup. Existing topics are left as they are. It is
    /// also the most messages a consumer handles at once: one per partition (ADR 0021).
    /// </summary>
    public int TopicPartitions { get; set; } = 12;

    /// <summary>Messages read ahead per partition while its worker is busy.</summary>
    public int PartitionQueueLength { get; set; } = 64;
}
