namespace Plaza.Payments;

public sealed record ChargeResult(bool Succeeded, string? ProviderReference, string? FailureReason)
{
    public static ChargeResult Success(string providerReference) => new(true, providerReference, null);

    public static ChargeResult Failure(string reason) => new(false, null, reason);
}

/// <summary>The payment provider as seen by Plaza: it charges a token, never a card.</summary>
public interface IPaymentProvider
{
    Task<ChargeResult> ChargeAsync(string paymentToken, long amountCents, string currency, CancellationToken cancellationToken);
}

/// <summary>
/// Stand-in for a real provider. The token decides the outcome, the way providers' test tokens
/// do: <c>tok_test_declined</c> and <c>tok_test_insufficient_funds</c> fail, any other
/// <c>tok_test_*</c> token succeeds, and everything else is not a token this provider issued.
/// </summary>
public sealed class SimulatedPaymentProvider : IPaymentProvider
{
    public const string TokenPrefix = "tok_test_";
    public const string DeclinedToken = "tok_test_declined";
    public const string InsufficientFundsToken = "tok_test_insufficient_funds";

    public Task<ChargeResult> ChargeAsync(
        string paymentToken, long amountCents, string currency, CancellationToken cancellationToken)
    {
        var result = paymentToken switch
        {
            _ when amountCents <= 0 => ChargeResult.Failure("invalid_amount"),
            DeclinedToken => ChargeResult.Failure("card_declined"),
            InsufficientFundsToken => ChargeResult.Failure("insufficient_funds"),
            _ when paymentToken.StartsWith(TokenPrefix, StringComparison.Ordinal) =>
                ChargeResult.Success($"sim_{Guid.NewGuid():N}"),
            _ => ChargeResult.Failure("invalid_token"),
        };
        return Task.FromResult(result);
    }
}
