using Plaza.Payments;
using Xunit;

namespace Plaza.UnitTests;

public sealed class SimulatedPaymentProviderTests
{
    private readonly SimulatedPaymentProvider _provider = new();

    [Fact]
    public async Task Test_token_is_charged()
    {
        var result = await _provider.ChargeAsync("tok_test_visa", 1000, "USD", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.StartsWith("sim_", result.ProviderReference);
        Assert.Null(result.FailureReason);
    }

    [Theory]
    [InlineData(SimulatedPaymentProvider.DeclinedToken, "card_declined")]
    [InlineData(SimulatedPaymentProvider.InsufficientFundsToken, "insufficient_funds")]
    [InlineData("tok_live_something", "invalid_token")]
    [InlineData("4242424242424242", "invalid_token")]
    public async Task Failing_tokens_report_their_reason(string token, string reason)
    {
        var result = await _provider.ChargeAsync(token, 1000, "USD", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(reason, result.FailureReason);
        Assert.Null(result.ProviderReference);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public async Task Non_positive_amount_is_never_charged(long amount)
    {
        var result = await _provider.ChargeAsync("tok_test_visa", amount, "USD", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_amount", result.FailureReason);
    }
}
