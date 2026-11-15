using Plaza.Messaging.Consuming;
using Xunit;

namespace Plaza.Messaging.Tests;

public sealed class RetryPolicyTests
{
    private static readonly TimeSpan Base = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(1, 100, 200)]
    [InlineData(2, 200, 400)]
    [InlineData(3, 400, 800)]
    [InlineData(4, 800, 1600)]
    [InlineData(5, 1600, 3200)]
    [InlineData(6, 2500, 5000)] // 6400 ms capped at 5 s
    [InlineData(500, 2500, 5000)] // no overflow for absurd attempt counts
    public void Delay_grows_exponentially_within_jitter_bounds_and_is_capped(int attempt, int minMs, int maxMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(minMs), RetryPolicy.Delay(attempt, Base, Max, jitter: 0));

        var upper = RetryPolicy.Delay(attempt, Base, Max, jitter: 0.999999);
        Assert.InRange(upper.TotalMilliseconds, minMs, maxMs);
    }

    [Fact]
    public void Delay_rejects_attempts_below_one() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.Delay(0, Base, Max, jitter: 0));
}
