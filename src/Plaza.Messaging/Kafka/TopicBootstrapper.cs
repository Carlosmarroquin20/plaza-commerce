using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plaza.Messaging.Consuming;

namespace Plaza.Messaging.Kafka;

/// <summary>
/// Creates the topics a service uses (and their dead-letter topics) before the consumer and
/// the publisher start, so nothing depends on broker-side auto-creation.
/// </summary>
internal sealed partial class TopicBootstrapper(
    MessageHandlerRegistry registry,
    IOptions<MessagingOptions> options,
    ILogger<TopicBootstrapper> logger) : IHostedService
{
    private const int MaxAttempts = 15;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var topics = registry.AllTopics;
        if (topics.Count == 0)
        {
            return;
        }

        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = options.Value.BootstrapServers }).Build();

        var specifications = topics
            .Select(t => new TopicSpecification { Name = t, NumPartitions = options.Value.TopicPartitions })
            .ToList();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await admin.CreateTopicsAsync(specifications);
                return;
            }
            catch (CreateTopicsException ex)
                when (ex.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
            {
                return;
            }
            catch (KafkaException ex) when (attempt < MaxAttempts)
            {
                // The broker may still be starting.
                LogWaitingForBroker(logger, ex.Error.Reason, attempt, MaxAttempts);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create topics ({Reason}); attempt {Attempt}/{MaxAttempts}")]
    private static partial void LogWaitingForBroker(ILogger logger, string reason, int attempt, int maxAttempts);
}
