using Plaza.PaymentProvider;
using Plaza.PaymentProvider.Data;
using Xunit;

namespace Plaza.UnitTests;

public sealed class ChargeSimulatorTests
{
    [Fact]
    public void Test_token_is_charged() =>
        Assert.Equal((ChargeStatus.Succeeded, null), ChargeSimulator.Decide("tok_test_visa", 1000));

    [Theory]
    [InlineData(ChargeSimulator.DeclinedToken, "card_declined")]
    [InlineData(ChargeSimulator.InsufficientFundsToken, "insufficient_funds")]
    [InlineData("tok_live_something", "invalid_token")]
    [InlineData("4242424242424242", "invalid_token")]
    public void Failing_tokens_report_their_reason(string token, string reason) =>
        Assert.Equal((ChargeStatus.Declined, reason), ChargeSimulator.Decide(token, 1000));

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public void Non_positive_amount_is_never_charged(long amount) =>
        Assert.Equal((ChargeStatus.Declined, "invalid_amount"), ChargeSimulator.Decide("tok_test_visa", amount));
}
