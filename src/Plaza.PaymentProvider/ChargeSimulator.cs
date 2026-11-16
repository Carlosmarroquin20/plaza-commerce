using Plaza.PaymentProvider.Data;

namespace Plaza.PaymentProvider;

/// <summary>
/// Decides the outcome of a charge from its token, the way real providers' test tokens do:
/// <c>tok_test_declined</c> and <c>tok_test_insufficient_funds</c> fail, any other
/// <c>tok_test_*</c> token succeeds, and everything else is not a token this provider issued.
/// </summary>
public static class ChargeSimulator
{
    public const string TokenPrefix = "tok_test_";
    public const string DeclinedToken = "tok_test_declined";
    public const string InsufficientFundsToken = "tok_test_insufficient_funds";

    public static (ChargeStatus Status, string? DeclineReason) Decide(string token, long amountCents) => token switch
    {
        _ when amountCents <= 0 => (ChargeStatus.Declined, "invalid_amount"),
        DeclinedToken => (ChargeStatus.Declined, "card_declined"),
        InsufficientFundsToken => (ChargeStatus.Declined, "insufficient_funds"),
        _ when token.StartsWith(TokenPrefix, StringComparison.Ordinal) => (ChargeStatus.Succeeded, null),
        _ => (ChargeStatus.Declined, "invalid_token"),
    };
}
