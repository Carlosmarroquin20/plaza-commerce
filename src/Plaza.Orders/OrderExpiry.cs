using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Plaza.Contracts;
using Plaza.Messaging;
using Plaza.Orders.Data;

namespace Plaza.Orders;

public sealed class CheckoutOptions
{
    /// <summary>
    /// How long a checkout may take. It is also the lifetime of the stock reservation: when it
    /// passes, the order expires and Inventory is told to release the stock.
    /// </summary>
    public TimeSpan ReservationTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How often overdue orders are looked for.</summary>
    public TimeSpan ExpirySweepInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int ExpiryBatchSize { get; set; } = 100;
}

/// <summary>
/// Expires orders whose checkout did not finish in time and starts their compensation
/// (ADR 0009). This is the only timer of the checkout: Inventory and Payments never give up
/// on an order by themselves, they are told.
/// </summary>
public sealed partial class OrderExpiryService(
    IServiceScopeFactory scopeFactory,
    IOptions<CheckoutOptions> options,
    TimeProvider timeProvider,
    ILogger<OrderExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(1);

    private readonly CheckoutOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var expired = await ExpireBatchAsync(stoppingToken);

                // A full batch means there is probably more waiting: sweep again at once.
                if (expired < _options.ExpiryBatchSize)
                {
                    await Task.Delay(_options.ExpirySweepInterval, timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Database unavailable: overdue orders stay as they are and are found next time.
                LogSweepFailed(logger, ex);
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

    internal async Task<int> ExpireBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The row locks decide races with the reply handlers: a handler that commits first
        // makes the row stop matching (it is re-checked after the lock is granted), and one
        // that commits later fails its version check, retries and finds the order expired.
        // SKIP LOCKED lets several instances sweep without waiting on each other.
        var due = await db.Database
            .SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM orders
                WHERE status IN ('Pending', 'AwaitingPayment') AND expires_at <= {now}
                ORDER BY expires_at
                LIMIT {_options.ExpiryBatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        var orders = await db.Orders.Where(o => due.Contains(o.Id)).ToListAsync(cancellationToken);
        var expired = 0;
        foreach (var order in orders)
        {
            if (order.Expire(now))
            {
                // The expiry and the message that undoes the order's side effects commit together.
                db.EnqueueMessage(
                    Topics.Orders, order.Id.ToString(), new OrderCancelled(order.Id, CancellationReasons.Expired));
                expired++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (expired > 0)
        {
            LogExpired(logger, expired);
        }

        return expired;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} order(s) that did not complete in time")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Expiring overdue orders failed; they will be retried")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
