using Plaza.Orders.Domain;
using Xunit;

namespace Plaza.UnitTests;

public sealed class OrderStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(1);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
    private static readonly DateTimeOffset AfterDeadline = Now + Ttl;

    private static Order NewOrder() => Order.Place(
        Guid.NewGuid(),
        "tok_test_ok",
        "USD",
        [
            new OrderItem { ProductId = Guid.NewGuid(), ProductName = "Desk", UnitPriceCents = 28900, Quantity = 1 },
            new OrderItem { ProductId = Guid.NewGuid(), ProductName = "Lamp", UnitPriceCents = 3200, Quantity = 2 },
        ],
        Now,
        Ttl);

    private static Order InStatus(OrderStatus status)
    {
        var order = NewOrder();
        switch (status)
        {
            case OrderStatus.Pending:
                break;
            case OrderStatus.AwaitingPayment:
                order.MarkStockReserved(Now, out _);
                break;
            case OrderStatus.Confirmed:
                order.MarkStockReserved(Now, out _);
                order.MarkPaymentSucceeded(Now);
                break;
            case OrderStatus.Rejected:
                order.MarkStockRejected("insufficient_stock", Now);
                break;
            case OrderStatus.PaymentFailed:
                order.MarkStockReserved(Now, out _);
                order.MarkPaymentFailed("card_declined", Now);
                break;
            case OrderStatus.Expired:
                order.MarkStockReserved(Now, out _);
                order.Expire(AfterDeadline);
                break;
        }

        Assert.Equal(status, order.Status);
        return order;
    }

    [Fact]
    public void New_order_is_pending_with_total_computed_from_items()
    {
        var order = NewOrder();

        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(28900 + (2 * 3200), order.TotalCents);
        Assert.Equal(7, order.Id.Version);
    }

    [Fact]
    public void Order_without_items_is_refused() =>
        Assert.Throws<ArgumentException>(() => Order.Place(Guid.NewGuid(), "tok_test_ok", "USD", [], Now, Ttl));

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(1, -1)]
    public void Order_with_invalid_item_is_refused(int quantity, long price) =>
        Assert.Throws<ArgumentException>(() => Order.Place(
            Guid.NewGuid(),
            "tok_test_ok",
            "USD",
            [new OrderItem { ProductId = Guid.NewGuid(), ProductName = "x", UnitPriceCents = price, Quantity = quantity }],
            Now,
            Ttl));

    [Fact]
    public void Total_overflow_fails_instead_of_wrapping() =>
        Assert.Throws<OverflowException>(() => Order.Place(
            Guid.NewGuid(),
            "tok_test_ok",
            "USD",
            [new OrderItem { ProductId = Guid.NewGuid(), ProductName = "x", UnitPriceCents = long.MaxValue, Quantity = 2 }],
            Now,
            Ttl));

    [Fact]
    public void Happy_path_reaches_confirmed()
    {
        var order = NewOrder();

        Assert.True(order.MarkStockReserved(Later, out var token));
        Assert.Equal("tok_test_ok", token);
        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);

        Assert.True(order.MarkPaymentSucceeded(Later));
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(Later, order.UpdatedAt);
        Assert.Null(order.FailureReason);
    }

    [Fact]
    public void Payment_token_is_forgotten_once_the_charge_is_requested()
    {
        var order = NewOrder();

        order.MarkStockReserved(Later, out _);

        Assert.Null(order.PaymentToken);
    }

    [Fact]
    public void Stock_rejection_ends_the_order_and_forgets_the_token()
    {
        var order = NewOrder();

        Assert.True(order.MarkStockRejected("insufficient_stock", Later));

        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal("insufficient_stock", order.FailureReason);
        Assert.Null(order.PaymentToken);
    }

    [Fact]
    public void Payment_failure_is_recorded_with_its_reason()
    {
        var order = InStatus(OrderStatus.AwaitingPayment);

        Assert.True(order.MarkPaymentFailed("card_declined", Later));

        Assert.Equal(OrderStatus.PaymentFailed, order.Status);
        Assert.Equal("card_declined", order.FailureReason);
    }

    [Fact]
    public void Oversized_failure_reason_is_truncated_instead_of_failing_the_transition()
    {
        var order = InStatus(OrderStatus.AwaitingPayment);

        order.MarkPaymentFailed(new string('x', 500), Later);

        Assert.Equal(Order.MaxFailureReasonLength, order.FailureReason!.Length);
    }

    // A reply for a step the order already took is a duplicate or a late delivery: ignore it.
    [Theory]
    [InlineData(OrderStatus.AwaitingPayment)]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.PaymentFailed)]
    public void Late_stock_reserved_is_ignored(OrderStatus status)
    {
        var order = InStatus(status);

        Assert.False(order.MarkStockReserved(Later, out _));
        Assert.Equal(status, order.Status);
        Assert.Equal(Now, order.UpdatedAt);
    }

    [Fact]
    public void Repeated_replies_are_ignored()
    {
        Assert.False(InStatus(OrderStatus.Rejected).MarkStockRejected("again", Later));
        Assert.False(InStatus(OrderStatus.Confirmed).MarkPaymentSucceeded(Later));
        Assert.False(InStatus(OrderStatus.PaymentFailed).MarkPaymentFailed("again", Later));
    }

    [Fact]
    public void Ignored_reply_does_not_overwrite_the_failure_reason()
    {
        var order = InStatus(OrderStatus.PaymentFailed);

        order.MarkPaymentFailed("another reason", Later);

        Assert.Equal("card_declined", order.FailureReason);
    }

    // A reply that contradicts the order's history must fail loudly so it is retried and
    // dead-lettered, never applied.
    [Theory]
    [InlineData(OrderStatus.Pending)] // payment result before stock was reserved
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.PaymentFailed)]
    public void Payment_success_out_of_sequence_is_invalid(OrderStatus status) =>
        Assert.Throws<InvalidOrderTransitionException>(() => InStatus(status).MarkPaymentSucceeded(Later));

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Confirmed)]
    public void Payment_failure_out_of_sequence_is_invalid(OrderStatus status) =>
        Assert.Throws<InvalidOrderTransitionException>(() => InStatus(status).MarkPaymentFailed("x", Later));

    [Theory]
    [InlineData(OrderStatus.AwaitingPayment)]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.PaymentFailed)]
    public void Stock_rejection_after_reservation_is_invalid(OrderStatus status) =>
        Assert.Throws<InvalidOrderTransitionException>(() => InStatus(status).MarkStockRejected("x", Later));

    [Fact]
    public void Deadline_is_set_when_the_order_is_placed() =>
        Assert.Equal(Now + Ttl, NewOrder().ExpiresAt);

    [Fact]
    public void Order_needs_a_positive_time_to_complete() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Order.Place(
            Guid.NewGuid(),
            "tok_test_ok",
            "USD",
            [new OrderItem { ProductId = Guid.NewGuid(), ProductName = "x", UnitPriceCents = 1, Quantity = 1 }],
            Now,
            TimeSpan.Zero));

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.AwaitingPayment)]
    public void Order_still_waiting_at_its_deadline_expires(OrderStatus status)
    {
        var order = InStatus(status);

        Assert.True(order.Expire(AfterDeadline));

        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal("expired", order.FailureReason);
        Assert.Null(order.PaymentToken);
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.AwaitingPayment)]
    public void Order_is_not_expired_before_its_deadline(OrderStatus status)
    {
        var order = InStatus(status);

        Assert.False(order.Expire(AfterDeadline.AddTicks(-1)));
        Assert.Equal(status, order.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.PaymentFailed)]
    [InlineData(OrderStatus.Expired)]
    public void Finished_order_never_expires(OrderStatus status)
    {
        var order = InStatus(status);
        var reason = order.FailureReason;

        Assert.False(order.Expire(AfterDeadline.AddDays(1)));
        Assert.Equal(status, order.Status);
        Assert.Equal(reason, order.FailureReason);
    }

    // Replies that arrive after the order expired are late, not contradictory: the
    // cancellation already undoes whatever they report.
    [Fact]
    public void Replies_after_expiry_are_ignored()
    {
        Assert.False(InStatus(OrderStatus.Expired).MarkStockReserved(Later, out _));
        Assert.False(InStatus(OrderStatus.Expired).MarkStockRejected("insufficient_stock", Later));
        Assert.False(InStatus(OrderStatus.Expired).MarkPaymentSucceeded(Later));
        Assert.False(InStatus(OrderStatus.Expired).MarkPaymentFailed("card_declined", Later));
    }

    [Fact]
    public void Stock_reserved_after_rejection_is_invalid() =>
        Assert.Throws<InvalidOrderTransitionException>(() => InStatus(OrderStatus.Rejected).MarkStockReserved(Later, out _));
}
