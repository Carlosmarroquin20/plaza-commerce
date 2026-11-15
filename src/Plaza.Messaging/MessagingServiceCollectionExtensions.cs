using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plaza.Messaging.Consuming;
using Plaza.Messaging.Kafka;
using Plaza.Messaging.Outbox;

namespace Plaza.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox publisher and the Kafka consumer for a service whose
    /// <typeparamref name="TDbContext"/> maps the messaging tables
    /// (<see cref="MessagingDbContextExtensions.AddMessagingTables"/>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="serviceName">Consumer group id; also identifies the service in the inbox.</param>
    /// <param name="configure">Optional overrides, applied after configuration.</param>
    public static MessagingBuilder AddPlazaMessaging<TDbContext>(
        this IServiceCollection services, string serviceName, Action<MessagingOptions>? configure = null)
        where TDbContext : DbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        // Read configuration when options are first resolved, not at registration, so that
        // hosts which add configuration late (tests) are honoured.
        services.AddOptions<MessagingOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.ServiceName = serviceName;
                options.BootstrapServers = configuration.GetConnectionString("kafka") ?? "";
                configure?.Invoke(options);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.BootstrapServers),
                "Kafka bootstrap servers are not configured (ConnectionStrings:kafka).")
            .Validate(o => o.MaxAttempts >= 1, "MaxAttempts must be at least 1.")
            .Validate(o => o.OutboxBatchSize >= 1, "OutboxBatchSize must be at least 1.");

        var registry = new MessageHandlerRegistry();
        services.AddSingleton(registry);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMessageProducer, KafkaMessageProducer>();

        // Hosted services start in registration order: topics first.
        services.AddHostedService<TopicBootstrapper>();
        services.AddHostedService<OutboxPublisher<TDbContext>>();
        services.AddHostedService<KafkaConsumerService<TDbContext>>();

        return new MessagingBuilder(services, registry);
    }
}

/// <summary>Declares the topics and handlers of a service.</summary>
public sealed class MessagingBuilder
{
    private readonly MessageHandlerRegistry _registry;

    internal MessagingBuilder(IServiceCollection services, MessageHandlerRegistry registry)
    {
        Services = services;
        _registry = registry;
    }

    public IServiceCollection Services { get; }

    /// <summary>Declares a topic this service publishes to, so it exists before first use.</summary>
    public MessagingBuilder Publishes(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        _registry.Publishes(topic);
        return this;
    }

    /// <summary>Subscribes to a topic. Message types without a handler are ignored.</summary>
    public MessagingBuilder Subscribe(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        _registry.Subscribe(topic);
        return this;
    }

    public MessagingBuilder Handle<TMessage, THandler>()
        where TMessage : IMessage
        where THandler : class, IMessageHandler<TMessage>
    {
        _registry.Add<TMessage, THandler>();
        Services.TryAddScoped<THandler>();
        return this;
    }
}
