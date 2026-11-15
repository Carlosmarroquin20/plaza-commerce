using Microsoft.Extensions.DependencyInjection;

namespace Plaza.Messaging.Consuming;

internal sealed record HandlerRegistration(
    Type MessageType,
    Func<IServiceProvider, object, MessageContext, CancellationToken, Task> InvokeAsync);

/// <summary>Topics and handlers declared by a service at startup.</summary>
internal sealed class MessageHandlerRegistry
{
    private readonly Dictionary<string, HandlerRegistration> _handlers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _publishedTopics = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Subscriptions => _subscriptions;

    /// <summary>Every topic this service touches, including dead-letter topics.</summary>
    public IReadOnlyCollection<string> AllTopics =>
        new HashSet<string>(
            [.. _publishedTopics, .. _subscriptions, .. _subscriptions.Select(MessagingDefaults.DeadLetterTopic)],
            StringComparer.Ordinal);

    public void Subscribe(string topic) => _subscriptions.Add(topic);

    public void Publishes(string topic) => _publishedTopics.Add(topic);

    public void Add<TMessage, THandler>()
        where TMessage : IMessage
        where THandler : class, IMessageHandler<TMessage>
    {
        var registration = new HandlerRegistration(
            typeof(TMessage),
            (services, message, context, cancellationToken) =>
                services.GetRequiredService<THandler>().HandleAsync((TMessage)message, context, cancellationToken));

        if (!_handlers.TryAdd(TMessage.MessageType, registration))
        {
            throw new InvalidOperationException($"A handler for '{TMessage.MessageType}' is already registered.");
        }
    }

    public bool TryGet(string messageType, out HandlerRegistration registration) =>
        _handlers.TryGetValue(messageType, out registration!);
}
