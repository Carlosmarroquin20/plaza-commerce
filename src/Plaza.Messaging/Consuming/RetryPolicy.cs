namespace Plaza.Messaging.Consuming;

internal static class RetryPolicy
{
    /// <summary>
    /// Exponential backoff with equal jitter: half of the exponential delay is fixed and half
    /// is random, so retries are spread out but never immediate.
    /// </summary>
    /// <param name="attempt">The attempt that just failed, starting at 1.</param>
    /// <param name="jitter">A value in [0, 1).</param>
    public static TimeSpan Delay(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        // Cap the exponent so the multiplication cannot overflow.
        var exponential = baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30));
        var capped = Math.Min(exponential, maxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds((capped / 2) + (capped / 2 * jitter));
    }
}
