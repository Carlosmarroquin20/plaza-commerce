using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Plaza.Payments.Data;

namespace Plaza.Payments;

public sealed record PaymentResponse(
    Guid Id, Guid OrderId, long AmountCents, string Currency, string Status, string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset? RefundedAt);

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        // Internal: lets support tooling and tests see what was charged for an order.
        app.MapGet("/payments/by-order/{orderId:guid}", GetByOrderAsync);
        return app;
    }

    private static async Task<Ok<List<PaymentResponse>>> GetByOrderAsync(
        Guid orderId, PaymentsDbContext db, CancellationToken cancellationToken)
    {
        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.OrderId == orderId)
            .Select(p => new PaymentResponse(
                p.Id, p.OrderId, p.AmountCents, p.Currency, p.Status.ToString(), p.FailureReason, p.CreatedAt, p.RefundedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(payments);
    }
}
