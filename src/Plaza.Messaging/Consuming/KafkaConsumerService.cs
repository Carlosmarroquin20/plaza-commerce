using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plaza.Messaging.Kafka;

namespace Plaza.Messaging.Consuming;

/// <summary>
/// Consumes the subscribed topics and applies each message exactly once (ADR 0005), retrying
/// failures in place and dead-lettering what cannot be processed (ADR 0006).
/// </summary>
internal sealed partial class KafkaConsumerService<TDbContext>(
    IServiceScopeFactory scopeFactory,
    MessageHandlerRegistry registry,
    IMessageProducer producer,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<KafkaConsumerService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DeadLetterRetryDelay = TimeSpan.FromSeconds(1);

    private readonly MessagingOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (registry.Subscriptions.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Consume() blocks, so the loop gets its own thread instead of stalling host startup.
        return Task.Run(() => RunAsync(stoppingToken), stoppingToken);
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ServiceName,
            ClientId = _options.ServiceName,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            // Offsets are committed in the background, but only those stored explicitly after
            // the message's database transaction committed.
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, error) => LogKafkaError(logger, error.Reason))
            .Build();

        consumer.Subscribe(registry.Subscriptions);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? result;
                try
                {
                    result = consumer.Consume(PollTimeout);
                }
                catch (ConsumeException ex)
                {
                    LogKafkaError(logger, ex.Error.Reason);
                    continue;
                }

                if (result?.Message is null)
                {
                    continue;
                }

                await ProcessAsync(result, stoppingToken);

                try
                {
                    consumer.StoreOffset(result);
                }
                catch (KafkaException ex)
                {
                    // The partition was revoked while processing. The new owner will receive
                    // the message again and the inbox will discard it.
                    LogKafkaError(logger, ex.Error.Reason);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: the message in flight was not stored and will be redelivered.
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task ProcessAsync(ConsumeResult<string, byte[]> result, CancellationToken cancellationToken)
    {
        var messageType = GetHeader(result, MessageHeaders.MessageType);
        if (messageType is null || !registry.TryGet(messageType, out var registration))
        {
            // Topics are shared by message types; this one is not for this service.
            return;
        }

        using var activity = StartActivity(result, messageType);

        if (!Guid.TryParse(GetHeader(result, MessageHeaders.MessageId), out var messageId))
        {
            await DeadLetterAsync(result, "missing-message-id", exception: null, attempts: 0, cancellationToken);
            return;
        }

        object message;
        try
        {
            message = JsonSerializer.Deserialize(result.Message.Value, registration.MessageType, MessagingDefaults.Json)
                ?? throw new JsonException("Message body is null.");
        }
        catch (JsonException ex)
        {
            // Retrying cannot fix a malformed payload.
            await DeadLetterAsync(result, "deserialization-failed", ex, attempts: 0, cancellationToken);
            return;
        }

        var occurredAt = DateTimeOffset.TryParse(
            GetHeader(result, MessageHeaders.OccurredAt), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : result.Message.Timestamp.UtcDateTime;
        var context = new MessageContext(messageId, messageType, result.Message.Key ?? "", occurredAt);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await HandleOnceAsync(registration, message, context, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= _options.MaxAttempts)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    await DeadLetterAsync(result, "handler-failed", ex, attempt, cancellationToken);
                    return;
                }

                var delay = RetryPolicy.Delay(
                    attempt, _options.RetryBaseDelay, _options.RetryMaxDelay, Random.Shared.NextDouble());
                LogRetry(logger, ex, messageType, messageId, attempt, _options.MaxAttempts, delay.TotalMilliseconds);
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
        }
    }

    private async Task HandleOnceAsync(
        HandlerRegistration registration, object message, MessageContext context, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The inbox row and the handler's changes commit together. If the row already exists
        // the message was applied before; a concurrent duplicate waits here on the row lock.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inbox_messages (consumer, message_id, processed_at)
            VALUES ({_options.ServiceName}, {context.MessageId}, {timeProvider.GetUtcNow()})
            ON CONFLICT DO NOTHING
            """, cancellationToken);

        if (inserted == 0)
        {
            LogDuplicate(logger, context.MessageType, context.MessageId);
            return;
        }

        await registration.InvokeAsync(scope.ServiceProvider, message, context, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task DeadLetterAsync(
        ConsumeResult<string, byte[]> result, string reason, Exception? exception, int attempts, CancellationToken cancellationToken)
    {
        var headers = new List<KeyValuePair<string, byte[]>>();
        foreach (var header in result.Message.Headers ?? [])
        {
            headers.Add(new(header.Key, header.GetValueBytes()));
        }

        headers.Add(new(MessageHeaders.DltReason, Encoding.UTF8.GetBytes(reason)));
        headers.Add(new(MessageHeaders.DltAttempts, Encoding.UTF8.GetBytes(attempts.ToString(CultureInfo.InvariantCulture))));
        headers.Add(new(MessageHeaders.DltOriginalTopic, Encoding.UTF8.GetBytes(result.Topic)));
        headers.Add(new(MessageHeaders.DltOriginalPartition, Encoding.UTF8.GetBytes(result.Partition.Value.ToString(CultureInfo.InvariantCulture))));
        headers.Add(new(MessageHeaders.DltOriginalOffset, Encoding.UTF8.GetBytes(result.Offset.Value.ToString(CultureInfo.InvariantCulture))));
        if (exception is not null)
        {
            // Type only: exception messages can contain data that does not belong in a topic.
            headers.Add(new(MessageHeaders.DltException, Encoding.UTF8.GetBytes(exception.GetType().FullName ?? "unknown")));
        }

        var deadLetter = new OutgoingMessage(
            MessagingDefaults.DeadLetterTopic(result.Topic), result.Message.Key ?? "", result.Message.Value ?? [], headers);

        LogDeadLetter(logger, exception, reason, result.Topic, result.Partition.Value, result.Offset.Value, attempts);

        // Never move past a message that is neither processed nor parked.
        while (true)
        {
            try
            {
                await producer.ProduceAsync([deadLetter], cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDeadLetterPublishFailed(logger, ex);
                await Task.Delay(DeadLetterRetryDelay, timeProvider, cancellationToken);
            }
        }
    }

    private static Activity? StartActivity(ConsumeResult<string, byte[]> result, string messageType)
    {
        // Continue the trace of whoever enqueued the message.
        ActivityContext.TryParse(GetHeader(result, MessageHeaders.TraceParent), traceState: null, isRemote: true, out var parent);

        var activity = MessagingDefaults.ActivitySource.StartActivity($"{messageType} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("messaging.destination.name", result.Topic);
        activity?.SetTag("messaging.message.id", GetHeader(result, MessageHeaders.MessageId));
        return activity;
    }

    private static string? GetHeader(ConsumeResult<string, byte[]> result, string name) =>
        result.Message.Headers is { } headers && headers.TryGetLastBytes(name, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kafka consumer error: {Reason}")]
    private static partial void LogKafkaError(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping duplicate {MessageType} {MessageId}")]
    private static partial void LogDuplicate(ILogger logger, string messageType, Guid messageId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Handling {MessageType} {MessageId} failed (attempt {Attempt}/{MaxAttempts}); retrying in {DelayMs} ms")]
    private static partial void LogRetry(
        ILogger logger, Exception exception, string messageType, Guid messageId, int attempt, int maxAttempts, double delayMs);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Dead-lettering message ({Reason}) from {Topic}[{Partition}]@{Offset} after {Attempts} attempts")]
    private static partial void LogDeadLetter(
        ILogger logger, Exception? exception, string reason, string topic, int partition, long offset, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing to the dead-letter topic failed; retrying")]
    private static partial void LogDeadLetterPublishFailed(ILogger logger, Exception exception);
}
