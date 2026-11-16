using Plaza.Contracts;
using Plaza.Messaging;
using Plaza.Orders.Data;
using Plaza.Orders.Domain;

namespace Plaza.Orders;

// Each handler advances the order state machine and, when the transition was applied, enqueues
// the next step. State change and outgoing message commit together with the inbox record.

internal static class OrderLookup
{
    public static async Task<Order> GetAsync(OrdersDbContext db, Guid orderId, CancellationToken cancellationToken) =>
        await db.Orders.FindAsync([orderId], cancellationToken)
        // Replies only exist for orders this service created, so this is a real anomaly:
        // fail, retry and eventually dead-letter rather than drop the reply.
        ?? throw new InvalidOperationException($"Order {orderId} does not exist.");
}

public sealed class StockReservedHandler(OrdersDbContext db, TimeProvider timeProvider) : IMessageHandler<StockReserved>
{
    public async Task HandleAsync(StockReserved message, MessageContext context, CancellationToken cancellationToken)
    {
        var order = await OrderLookup.GetAsync(db, message.OrderId, cancellationToken);
        if (order.MarkStockReserved(timeProvider.GetUtcNow(), out var paymentToken))
        {
            db.EnqueueMessage(
                Topics.Orders,
                order.Id.ToString(),
                new PaymentRequested(order.Id, order.TotalCents, order.Currency, paymentToken));
        }
    }
}

public sealed class StockRejectedHandler(OrdersDbContext db, TimeProvider timeProvider) : IMessageHandler<StockRejected>
{
    public async Task HandleAsync(StockRejected message, MessageContext context, CancellationToken cancellationToken)
    {
        var order = await OrderLookup.GetAsync(db, message.OrderId, cancellationToken);
        order.MarkStockRejected(message.Reason, timeProvider.GetUtcNow());
    }
}

public sealed class PaymentSucceededHandler(OrdersDbContext db, TimeProvider timeProvider) : IMessageHandler<PaymentSucceeded>
{
    public async Task HandleAsync(PaymentSucceeded message, MessageContext context, CancellationToken cancellationToken)
    {
        var order = await OrderLookup.GetAsync(db, message.OrderId, cancellationToken);
        if (order.MarkPaymentSucceeded(timeProvider.GetUtcNow()))
        {
            db.EnqueueMessage(
                Topics.Orders,
                order.Id.ToString(),
                new OrderConfirmed(order.Id, order.CustomerId, order.TotalCents, order.Currency));
        }
    }
}

public sealed class PaymentFailedHandler(OrdersDbContext db, TimeProvider timeProvider) : IMessageHandler<PaymentFailed>
{
    public async Task HandleAsync(PaymentFailed message, MessageContext context, CancellationToken cancellationToken)
    {
        var order = await OrderLookup.GetAsync(db, message.OrderId, cancellationToken);
        if (order.MarkPaymentFailed(message.Reason, timeProvider.GetUtcNow()))
        {
            // Compensation: the stock held for this order can be sold again (ADR 0009).
            db.EnqueueMessage(
                Topics.Orders, order.Id.ToString(), new OrderCancelled(order.Id, CancellationReasons.PaymentFailed));
        }
    }
}
