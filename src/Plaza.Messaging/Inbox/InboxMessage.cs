namespace Plaza.Messaging.Inbox;

/// <summary>Record of a message already applied by a consumer (ADR 0005).</summary>
public sealed class InboxMessage
{
    public required string Consumer { get; init; }

    public required Guid MessageId { get; init; }

    public required DateTimeOffset ProcessedAt { get; init; }
}
