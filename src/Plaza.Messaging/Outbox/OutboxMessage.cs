namespace Plaza.Messaging.Outbox;

/// <summary>A message waiting to be published. Written in the same transaction as the business change.</summary>
public sealed class OutboxMessage
{
    /// <summary>Insertion order; the publisher reads in this order.</summary>
    public long Sequence { get; private set; }

    /// <summary>Message id sent to consumers, which deduplicate on it.</summary>
    public required Guid Id { get; init; }

    public required string Topic { get; init; }

    public required string Key { get; init; }

    public required string Type { get; init; }

    public required string Payload { get; init; }

    /// <summary>W3C trace context of the operation that produced the message.</summary>
    public string? TraceParent { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? PublishedAt { get; set; }
}
