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

        var result = await provider.ChargeAsync(
            message.PaymentToken, message.AmountCents, message.Currency, cancellationToken);

        var payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            OrderId = message.OrderId,
            AmountCents = message.AmountCents,
            Currency = message.Currency,
            Status = result.Succeeded ? PaymentStatus.Succeeded : PaymentStatus.Failed,
            ProviderReference = result.ProviderReference,
            FailureReason = result.FailureReason,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.Payments.Add(payment);

        var key = message.OrderId.ToString();
        if (result.Succeeded)
        {
            db.EnqueueMessage(Topics.Payments, key, new PaymentSucceeded(message.OrderId, payment.Id));
        }
        else
        {
            db.EnqueueMessage(Topics.Payments, key, new PaymentFailed(message.OrderId, result.FailureReason ?? "unknown"));
        }
    }
}
