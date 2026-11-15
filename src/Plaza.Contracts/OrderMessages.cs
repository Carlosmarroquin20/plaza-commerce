using Plaza.Messaging;

namespace Plaza.Contracts;

// Published by Orders on Topics.Orders.

public sealed record OrderLine(Guid ProductId, int Quantity);

/// <summary>An order was accepted and needs its stock reserved.</summary>
public sealed record OrderPlaced(Guid OrderId, IReadOnlyList<OrderLine> Lines) : IMessage
{
    public static string MessageType => "orders.order-placed.v1";
}

/// <summary>Stock is reserved; charge the customer.</summary>
/// <param name="PaymentToken">Opaque token issued by the payment provider. Never card data.</param>
public sealed record PaymentRequested(Guid OrderId, long AmountCents, string Currency, string PaymentToken) : IMessage
{
    public static string MessageType => "orders.payment-requested.v1";
}

/// <summary>The order is paid and final.</summary>
public sealed record OrderConfirmed(Guid OrderId, Guid CustomerId, long TotalCents, string Currency) : IMessage
{
    public static string MessageType => "orders.order-confirmed.v1";
}
