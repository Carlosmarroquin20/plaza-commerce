using Microsoft.EntityFrameworkCore;
using Plaza.Contracts;
using Plaza.Messaging;
using Plaza.Payments.Data;

namespace Plaza.Payments;

/// <summary>Charges an order and reports the outcome.</summary>
public sealed class PaymentRequestedHandler(PaymentsDbContext db, IPaymentProvider provider, TimeProvider timeProvider)
    : IMessageHandler<PaymentRequested>
{
    public async Task HandleAsync(PaymentRequested message, MessageContext context, CancellationToken cancellationToken)
    {
        // The inbox already discards redeliveries of this message. This guards against a
        // different message asking to charge the same order again.
        if (await db.Payments.AnyAsync(p => p.OrderId == message.OrderId, cancellationToken))
        {
            return;
        }

        // The provider is outside this handler's transaction: if the call succeeds and the
        // transaction then fails, or the answer is lost, the message is handled again. The
        // idempotency key makes that second call return the first outcome instead of charging
        // twice (ADR 0010). A timeout or transport error propagates and is retried.
        var result = await provider.ChargeAsync(
            PaymentKeys.ForOrder(message.OrderId),
            message.OrderId,
            message.PaymentToken,
            message.AmountCents,
            message.Currency,
            cancellationToken);

        var succeeded = result.Outcome == ChargeOutcome.Succeeded;
        var payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            OrderId = message.OrderId,
            AmountCents = message.AmountCents,
            Currency = message.Currency,
            Status = succeeded ? PaymentStatus.Succeeded : PaymentStatus.Failed,
            ProviderReference = result.ProviderReference,
            FailureReason = result.FailureReason,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.Payments.Add(payment);

        var key = message.OrderId.ToString();
        if (succeeded)
        {
            db.EnqueueMessage(Topics.Payments, key, new PaymentSucceeded(message.OrderId, payment.Id));
        }
        else
        {
            db.EnqueueMessage(Topics.Payments, key, new PaymentFailed(message.OrderId, result.FailureReason ?? "unknown"));
        }
    }
}

/// <summary>
/// Compensation: the order will not complete, so no money may be kept for it. Messages of one
/// order share a partition, so this runs after the order's <see cref="PaymentRequested"/> was
/// handled or dead-lettered.
/// </summary>
public sealed partial class OrderCancelledHandler(
    PaymentsDbContext db, IPaymentProvider provider, TimeProvider timeProvider, ILogger<OrderCancelledHandler> logger)
    : IMessageHandler<OrderCancelled>
{
    public async Task HandleAsync(OrderCancelled message, MessageContext context, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.OrderId == message.OrderId, cancellationToken);
        if (payment is { Status: PaymentStatus.Failed or PaymentStatus.Refunded })
        {
            return; // No money is held.
        }

        // Either the charge succeeded, or this service does not know what happened to it (the
        // provider may have charged although every answer was lost). Ask the provider: it
        // refunds the charge if there is one and blocks the key if there is none.
        var refunded = await provider.VoidAsync(PaymentKeys.ForOrder(message.OrderId), cancellationToken);

        if (payment is not null)
        {
            payment.Status = PaymentStatus.Refunded;
            payment.RefundedAt = timeProvider.GetUtcNow();
        }
        else if (refunded)
        {
            LogUnrecordedChargeRefunded(logger, message.OrderId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Order {OrderId} was charged by the provider without a payment record; the charge was refunded")]
    private static partial void LogUnrecordedChargeRefunded(ILogger logger, Guid orderId);
}
