using Plaza.Contracts;
using Plaza.Inventory.Data;
using Plaza.Messaging;

namespace Plaza.Inventory;

/// <summary>Reserves stock for a new order and reports the outcome.</summary>
public sealed class OrderPlacedHandler(InventoryDbContext db, StockReservationService reservations)
    : IMessageHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced message, MessageContext context, CancellationToken cancellationToken)
    {
        var lines = message.Lines.Select(l => new ReservationLine(l.ProductId, l.Quantity)).ToList();
        var outcome = await reservations.TryReserveAsync(message.OrderId, lines, cancellationToken);

        var key = message.OrderId.ToString();
        switch (outcome)
        {
            case ReservationOutcome.Reserved:
                db.EnqueueMessage(Topics.Inventory, key, new StockReserved(message.OrderId));
                break;
            case ReservationOutcome.InsufficientStock:
                db.EnqueueMessage(Topics.Inventory, key, new StockRejected(message.OrderId, "insufficient_stock"));
                break;
            default:
                db.EnqueueMessage(Topics.Inventory, key, new StockRejected(message.OrderId, "invalid_order"));
                break;
        }
    }
}

/// <summary>The order is paid: its reserved units leave the stock for good.</summary>
public sealed class OrderConfirmedHandler(StockReservationService reservations) : IMessageHandler<OrderConfirmed>
{
    public Task HandleAsync(OrderConfirmed message, MessageContext context, CancellationToken cancellationToken) =>
        reservations.CommitAsync(message.OrderId, cancellationToken);
}
