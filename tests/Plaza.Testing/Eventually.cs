namespace Plaza.Testing;

/// <summary>Polls for a condition instead of sleeping for a fixed time.</summary>
public static class Eventually
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static async Task<T> GetAsync<T>(
        Func<Task<T>> probe, Func<T, bool> isDone, string description, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        T last;
        do
        {
            last = await probe();
            if (isDone(last))
            {
                return last;
            }

            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Timed out waiting for: {description}. Last value: {last}");
    }

    public static Task UntilAsync(Func<Task<bool>> condition, string description, TimeSpan? timeout = null) =>
        GetAsync(condition, done => done, description, timeout);
}
