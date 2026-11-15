using Microsoft.EntityFrameworkCore;
using Plaza.Contracts;
using Plaza.Messaging;
using Plaza.Notifications.Data;

namespace Plaza.Notifications;

public static class NotificationKinds
{
    public const string OrderConfirmed = "order_confirmed";
}

/// <summary>
/// Tells the customer their order is confirmed. Delivery is simulated: the notification is
/// recorded and logged. A real channel (email, push) would be called here.
/// </summary>
public sealed partial class OrderConfirmedHandler(
    NotificationsDbContext db, TimeProvider timeProvider, ILogger<OrderConfirmedHandler> logger)
    : IMessageHandler<OrderConfirmed>
{
    public async Task HandleAsync(OrderConfirmed message, MessageContext context, CancellationToken cancellationToken)
    {
        var alreadySent = await db.Notifications.AnyAsync(
            n => n.OrderId == message.OrderId && n.Kind == NotificationKinds.OrderConfirmed, cancellationToken);
        if (alreadySent)
        {
            return;
        }

        db.Notifications.Add(new Notification
        {
            Id = Guid.CreateVersion7(),
            OrderId = message.OrderId,
            CustomerId = message.CustomerId,
            Kind = NotificationKinds.OrderConfirmed,
            CreatedAt = timeProvider.GetUtcNow(),
        });

        LogSent(logger, message.OrderId, message.CustomerId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} confirmed: notifying customer {CustomerId}")]
    private static partial void LogSent(ILogger logger, Guid orderId, Guid customerId);
}
