namespace Plaza.Messaging;

/// <summary>A message that travels through Kafka.</summary>
public interface IMessage
{
    /// <summary>
    /// Stable, versioned name used for routing, for example <c>orders.order-placed.v1</c>.
    /// Never derived from the CLR type name, so types can be renamed freely.
    /// </summary>
    static abstract string MessageType { get; }
}

/// <summary>Handles one message type. Resolved from a DI scope per delivery attempt.</summary>
public interface IMessageHandler<in TMessage>
    where TMessage : IMessage
{
    /// <summary>
    /// Runs inside the consumer's database transaction. Changes made through the scoped
    /// DbContext, including enqueued messages, are saved and committed by the consumer together
    /// with the inbox record. Throwing rolls everything back and triggers a retry.
    /// </summary>
    Task HandleAsync(TMessage message, MessageContext context, CancellationToken cancellationToken);
}

/// <summary>Metadata of the message being handled.</summary>
public sealed record MessageContext(Guid MessageId, string MessageType, string Key, DateTimeOffset OccurredAt);
