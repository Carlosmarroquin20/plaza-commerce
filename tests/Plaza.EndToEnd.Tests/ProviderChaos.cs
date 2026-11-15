namespace Plaza.EndToEnd.Tests;

/// <summary>What goes wrong with one call from Payments to the payment provider.</summary>
public enum Fault
{
    None,

    /// <summary>The provider is down: the request never reaches it.</summary>
    Unavailable,

    /// <summary>
    /// The provider is slow: it processes the request, but the answer does not come back before
    /// the caller's timeout. The caller cannot tell this from a request that was never processed.
    /// </summary>
    AnswerLost,
}

/// <summary>
/// Decides, per charge request, whether to inject a fault. Only charges are affected; voids
/// always go through, so compensation can be observed.
/// </summary>
public sealed class ProviderChaos
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (int Ordinal, int Attempts)> _charges = [];
    private volatile Func<int, int, Fault> _plan = (_, _) => Fault.None;
    private int _unavailable;
    private int _answersLost;

    /// <summary>Charge requests that never reached the provider.</summary>
    public int Unavailable => Volatile.Read(ref _unavailable);

    /// <summary>Charge requests the provider processed but whose answer the caller never got.</summary>
    public int AnswersLost => Volatile.Read(ref _answersLost);

    /// <summary>Back to a healthy provider, with counters at zero.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _plan = (_, _) => Fault.None;
            _charges.Clear();
            _unavailable = 0;
            _answersLost = 0;
        }
    }

    /// <summary>Every charge request gets this fault until <see cref="Reset"/>.</summary>
    public void Always(Fault fault) => _plan = (_, _) => fault;

    /// <summary>The first <paramref name="times"/> requests of each charge get this fault.</summary>
    public void FirstAttempts(Fault fault, int times) => _plan = (_, attempt) => attempt < times ? fault : Fault.None;

    /// <summary>
    /// Full control: <paramref name="plan"/> receives the ordinal of the charge (0 for the first
    /// idempotency key seen, 1 for the second, ...) and the attempt number for that charge (0 for
    /// the first request with that key).
    /// </summary>
    public void Plan(Func<int, int, Fault> plan) => _plan = plan;

    internal Fault Next(string idempotencyKey)
    {
        Fault fault;
        lock (_gate)
        {
            var (ordinal, attempt) = _charges.TryGetValue(idempotencyKey, out var seen) ? seen : (_charges.Count, 0);
            _charges[idempotencyKey] = (ordinal, attempt + 1);
            fault = _plan(ordinal, attempt);
        }

        switch (fault)
        {
            case Fault.Unavailable:
                Interlocked.Increment(ref _unavailable);
                break;
            case Fault.AnswerLost:
                Interlocked.Increment(ref _answersLost);
                break;
        }

        return fault;
    }
}
/// <summary>Sits between Payments' HTTP client and the provider and applies <see cref="ProviderChaos"/>.</summary>
public sealed class ChaosHandler(ProviderChaos chaos, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isCharge = request.Method == HttpMethod.Post
            && request.Headers.TryGetValues(Payments.HttpPaymentProvider.IdempotencyKeyHeader, out _);
        if (!isCharge)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var key = request.Headers.GetValues(Payments.HttpPaymentProvider.IdempotencyKeyHeader).Single();
        switch (chaos.Next(key))
        {
            case Fault.Unavailable:
                throw new HttpRequestException("Injected fault: the payment provider is unreachable.");

            case Fault.AnswerLost:
                // The provider does the work...
                using (await base.SendAsync(request, cancellationToken))
                {
                }

                // ...and the caller waits until its own timeout cancels the call.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable: the delay only ends by cancellation.");

            default:
                return await base.SendAsync(request, cancellationToken);
        }
    }
}
