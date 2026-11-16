namespace Plaza.Payments;

public enum ChargeOutcome
{
    /// <summary>Money was taken.</summary>
    Succeeded,

    /// <summary>The provider refused the charge.</summary>
    Declined,

    /// <summary>The charge was cancelled at the provider before or after it happened; no money is held.</summary>
    Voided,
}

public sealed record ChargeResult(ChargeOutcome Outcome, string? ProviderReference, string? FailureReason);

/// <summary>The payment provider as seen by Plaza: it charges a token, never a card.</summary>
public interface IPaymentProvider
{
    /// <summary>
    /// Charges once per <paramref name="idempotencyKey"/>. Calling again with the same key
    /// returns the first outcome, so a call whose answer was lost can simply be repeated.
    /// </summary>
    Task<ChargeResult> ChargeAsync(
        string idempotencyKey, Guid orderId, string paymentToken, long amountCents, string currency, CancellationToken cancellationToken);

    /// <summary>
    /// Makes sure no money is held for <paramref name="idempotencyKey"/>: refunds its charge if
    /// there is one and refuses any charge that arrives later. Safe to repeat.
    /// </summary>
    /// <returns><c>true</c> when a charge had taken money and it was refunded.</returns>
    Task<bool> VoidAsync(string idempotencyKey, CancellationToken cancellationToken);
}

public static class PaymentKeys
{
    /// <summary>
    /// The idempotency key of an order's charge. Derived from the order id alone, so every
    /// attempt to charge an order, from any message or any instance, uses the same key.
    /// </summary>
    public static string ForOrder(Guid orderId) => $"plaza-order-{orderId:D}";
}

/// <summary>
/// Calls the provider's HTTP API. Timeouts and transport errors are thrown, never turned into
/// a failed payment: the caller cannot know whether the charge happened and must retry with
/// the same key.
/// </summary>
public sealed class HttpPaymentProvider(HttpClient http) : IPaymentProvider
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public async Task<ChargeResult> ChargeAsync(
        string idempotencyKey, Guid orderId, string paymentToken, long amountCents, string currency, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/charges")
        {
            Content = JsonContent.Create(new ProviderChargeRequest(paymentToken, amountCents, currency, orderId.ToString("D"))),
        };
        request.Headers.Add(IdempotencyKeyHeader, idempotencyKey);

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var charge = await ReadAsync(response, cancellationToken);

        return charge.Status switch
        {
            "Succeeded" => new ChargeResult(ChargeOutcome.Succeeded, charge.Id.ToString("N"), null),
            "Declined" => new ChargeResult(ChargeOutcome.Declined, null, charge.DeclineReason ?? "declined"),
            "Voided" or "Refunded" => new ChargeResult(ChargeOutcome.Voided, null, "order_cancelled"),
            _ => throw new InvalidOperationException($"Unknown charge status '{charge.Status}' from the payment provider."),
        };
    }

    public async Task<bool> VoidAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsync(
            $"/charges/by-key/{Uri.EscapeDataString(idempotencyKey)}/void", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await ReadAsync(response, cancellationToken)).Status == "Refunded";
    }

    private static async Task<ProviderCharge> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<ProviderCharge>(cancellationToken)
        ?? throw new InvalidOperationException("The payment provider returned an empty response.");

    private sealed record ProviderChargeRequest(string Token, long AmountCents, string Currency, string Reference);

    private sealed record ProviderCharge(Guid Id, string Status, string? DeclineReason);
}
