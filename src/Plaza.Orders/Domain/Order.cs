namespace Plaza.Orders.Domain;

public enum OrderStatus
{
    /// <summary>Accepted; waiting for Inventory to reserve stock.</summary>
    Pending,

    /// <summary>Stock reserved; waiting for Payments to charge.</summary>
    AwaitingPayment,

    /// <summary>Paid. Final.</summary>
    Confirmed,

    /// <summary>Stock could not be reserved. Final.</summary>
    Rejected,

    /// <summary>The charge was declined. Final; the reserved stock is released.</summary>
    PaymentFailed,

    /// <summary>
    /// The checkout did not finish before the order's deadline. Final; the reserved stock is
    /// released and any charge is refunded.
    /// </summary>
    Expired,
}

/// <summary>A reply arrived that contradicts the order's history (ADR 0007).</summary>
public sealed class InvalidOrderTransitionException(Guid orderId, OrderStatus status, string trigger)
    : InvalidOperationException($"Order {orderId} in status {status} cannot handle '{trigger}'.");

public sealed class OrderItem
{
    public required Guid ProductId { get; init; }

    /// <summary>Name at the time of purchase; the catalogue may change later.</summary>
    public required string ProductName { get; init; }

    /// <summary>Price at the time of purchase, in minor units.</summary>
    public required long UnitPriceCents { get; init; }

    public required int Quantity { get; init; }
}

/// <summary>
/// The checkout state machine:
/// <code>
/// Pending --StockReserved--> AwaitingPayment --PaymentSucceeded--> Confirmed
///    |                              |
///    +--StockRejected--> Rejected   +--PaymentFailed--> PaymentFailed
///
/// Pending or AwaitingPayment --deadline passed--> Expired
/// </code>
/// Each transition returns <c>true</c> when it was applied and <c>false</c> when the order had
/// already moved past it (a duplicate or late reply, safe to ignore). A reply that cannot
/// belong to this order's history throws <see cref="InvalidOrderTransitionException"/>.
/// </summary>
public sealed class Order
{
    public const int MaxFailureReasonLength = 128;

    private readonly List<OrderItem> _items = [];

    private Order()
    {
    }

    public Guid Id { get; private set; }

    public Guid CustomerId { get; private set; }

    public OrderStatus Status { get; private set; }

    public long TotalCents { get; private set; }

    public string Currency { get; private set; } = "";

    /// <summary>
    /// Opaque payment-provider token, kept only until the charge is requested. Never card data.
    /// </summary>
    public string? PaymentToken { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Deadline of the checkout. An order still waiting for stock or payment after it is
    /// expired, which also ends its stock reservation (ADR 0009).
    /// </summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>; makes concurrent updates of one order fail instead of overwrite.</summary>
    public uint Version { get; private set; }

    public IReadOnlyList<OrderItem> Items => _items;

    public static Order Place(
        Guid customerId,
        string paymentToken,
        string currency,
        IReadOnlyCollection<OrderItem> items,
        DateTimeOffset now,
        TimeSpan timeToComplete)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToComplete, TimeSpan.Zero);

        if (items.Count == 0)
        {
            throw new ArgumentException("An order needs at least one item.", nameof(items));
        }

        if (items.Any(i => i.Quantity <= 0 || i.UnitPriceCents < 0))
        {
            throw new ArgumentException("Items need a positive quantity and a non-negative price.", nameof(items));
        }

        var order = new Order
        {
            Id = Guid.CreateVersion7(now),
            CustomerId = customerId,
            Status = OrderStatus.Pending,
            Currency = currency,
            PaymentToken = paymentToken,
            // checked: an overflow must fail the request, not wrap into a small total.
            TotalCents = checked(items.Sum(i => checked(i.UnitPriceCents * i.Quantity))),
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + timeToComplete,
        };
        order._items.AddRange(items);
        return order;
    }

    /// <summary>Stock is held. Returns the token to charge, which the order then forgets.</summary>
    public bool MarkStockReserved(DateTimeOffset now, out string paymentToken)
    {
        paymentToken = "";
        switch (Status)
        {
            case OrderStatus.Pending:
                paymentToken = PaymentToken
                    ?? throw new InvalidOperationException($"Order {Id} has no payment token.");
                PaymentToken = null;
                MoveTo(OrderStatus.AwaitingPayment, now);
                return true;
            case OrderStatus.AwaitingPayment or OrderStatus.Confirmed or OrderStatus.PaymentFailed or OrderStatus.Expired:
                return false;
            default:
                throw new InvalidOrderTransitionException(Id, Status, nameof(MarkStockReserved));
        }
    }

    public bool MarkStockRejected(string reason, DateTimeOffset now)
    {
        switch (Status)
        {
            case OrderStatus.Pending:
                PaymentToken = null;
                FailureReason = Truncate(reason);
                MoveTo(OrderStatus.Rejected, now);
                return true;
            case OrderStatus.Rejected or OrderStatus.Expired:
                return false;
            default:
                throw new InvalidOrderTransitionException(Id, Status, nameof(MarkStockRejected));
        }
    }

    public bool MarkPaymentSucceeded(DateTimeOffset now)
    {
        switch (Status)
        {
            case OrderStatus.AwaitingPayment:
                MoveTo(OrderStatus.Confirmed, now);
                return true;
            // Expired: the cancellation already told Payments to refund this charge.
            case OrderStatus.Confirmed or OrderStatus.Expired:
                return false;
            default:
                throw new InvalidOrderTransitionException(Id, Status, nameof(MarkPaymentSucceeded));
        }
    }

    public bool MarkPaymentFailed(string reason, DateTimeOffset now)
    {
        switch (Status)
        {
            case OrderStatus.AwaitingPayment:
                FailureReason = Truncate(reason);
                MoveTo(OrderStatus.PaymentFailed, now);
                return true;
            case OrderStatus.PaymentFailed or OrderStatus.Expired:
                return false;
            default:
                throw new InvalidOrderTransitionException(Id, Status, nameof(MarkPaymentFailed));
        }
    }

    /// <summary>
    /// Gives up on an order that is still waiting once its deadline has passed. Returns
    /// <c>false</c> for an order that already finished or is not due yet.
    /// </summary>
    public bool Expire(DateTimeOffset now)
    {
        if (Status is not (OrderStatus.Pending or OrderStatus.AwaitingPayment) || now < ExpiresAt)
        {
            return false;
        }

        PaymentToken = null;
        FailureReason = "expired";
        MoveTo(OrderStatus.Expired, now);
        return true;
    }

    /// <summary>Reasons come from other services; never let an oversized one fail the transition.</summary>
    private static string Truncate(string reason) =>
        reason.Length <= MaxFailureReasonLength ? reason : reason[..MaxFailureReasonLength];

    private void MoveTo(OrderStatus status, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
    }
}
