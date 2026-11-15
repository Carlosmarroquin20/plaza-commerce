using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plaza.Messaging.Kafka;

namespace Plaza.Messaging.Outbox;

/// <summary>
/// Publishes outbox rows to Kafka (ADR 0004). At-least-once: a batch whose transaction does
/// not commit after the broker acknowledged it is published again on the next poll.
/// </summary>
internal sealed partial class OutboxPublisher<TDbContext>(
    IServiceScopeFactory scopeFactory,
    IMessageProducer producer,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxPublisher<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext
{
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(1);

    private readonly MessagingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishBatchAsync(stoppingToken);

                // A full batch means there is probably more waiting: poll again at once.
                if (published < _options.OutboxBatchSize)
                {
                    await Task.Delay(_options.OutboxPollInterval, timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Database or broker unavailable: the rows stay unpublished and are retried.
                LogPublishFailed(logger, ex);
                try
                {
                    await Task.Delay(ErrorDelay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    internal async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // SKIP LOCKED lets several publisher instances work without blocking each other
        // or publishing the same row concurrently.
        var batch = await db.Set<OutboxMessage>()
            .FromSqlInterpolated($"""
                SELECT * FROM outbox_messages
                WHERE published_at IS NULL
                ORDER BY sequence
                LIMIT {_options.OutboxBatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            return 0;
        }

        await producer.ProduceAsync(batch.ConvertAll(ToOutgoing), cancellationToken);

        var now = timeProvider.GetUtcNow();
        foreach (var message in batch)
        {
            message.PublishedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return batch.Count;
    }

    private static OutgoingMessage ToOutgoing(OutboxMessage message)
    {
        var headers = new List<KeyValuePair<string, byte[]>>(4)
        {
            new(MessageHeaders.MessageId, Encoding.UTF8.GetBytes(message.Id.ToString())),
            new(MessageHeaders.MessageType, Encoding.UTF8.GetBytes(message.Type)),
            new(MessageHeaders.OccurredAt, Encoding.UTF8.GetBytes(message.OccurredAt.ToString("O"))),
        };
        if (message.TraceParent is not null)
        {
            headers.Add(new(MessageHeaders.TraceParent, Encoding.UTF8.GetBytes(message.TraceParent)));
        }

        return new OutgoingMessage(message.Topic, message.Key, Encoding.UTF8.GetBytes(message.Payload), headers);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox publish failed; unpublished messages will be retried")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception);
}
