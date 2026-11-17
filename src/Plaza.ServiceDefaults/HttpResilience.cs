using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Plaza.ServiceDefaults;

/// <summary>
/// What every outgoing HTTP call between Plaza services gets (ADR 0022), from the outside in:
/// a limit on the whole call, retries, a circuit breaker and a limit per attempt.
/// </summary>
public static class HttpResilience
{
    /// <summary>Settings under <c>Resilience</c>; the defaults suit calls that normally take milliseconds.</summary>
    public sealed class Settings
    {
        /// <summary>How long one attempt may take before it is abandoned.</summary>
        public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>How long the call may take in total, retries and waits included.</summary>
        public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Extra attempts after a failed one. Only for requests that are safe to repeat.</summary>
        public int Retries { get; set; } = 2;

        public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>The share of failed calls, within the sampling window, that opens the circuit.</summary>
        public double BreakerFailureRatio { get; set; } = 0.5;

        public TimeSpan BreakerSamplingDuration { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Calls needed in the window before the ratio means anything.</summary>
        public int BreakerMinimumThroughput { get; set; } = 20;

        /// <summary>How long the circuit stays open, failing calls at once, before one is let through to test.</summary>
        public TimeSpan BreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(5);
    }

    public static IHttpClientBuilder AddPlazaResilience(this IHttpClientBuilder builder, IConfiguration configuration)
    {
        var settings = configuration.GetSection("Resilience").Get<Settings>() ?? new Settings();
        return builder.AddPlazaResilience(settings);
    }

    public static IHttpClientBuilder AddPlazaResilience(this IHttpClientBuilder builder, Settings settings)
    {
        builder.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = settings.TotalTimeout;
            options.AttemptTimeout.Timeout = settings.AttemptTimeout;

            options.Retry.MaxRetryAttempts = settings.Retries;
            options.Retry.Delay = settings.RetryBaseDelay;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            // GET, HEAD, PUT and DELETE can be repeated. A POST is repeated only by a caller
            // that knows it is safe, such as the payment handler with its idempotency key.
            options.Retry.DisableForUnsafeHttpMethods();

            options.CircuitBreaker.FailureRatio = settings.BreakerFailureRatio;
            options.CircuitBreaker.SamplingDuration = settings.BreakerSamplingDuration;
            options.CircuitBreaker.MinimumThroughput = settings.BreakerMinimumThroughput;
            options.CircuitBreaker.BreakDuration = settings.BreakerBreakDuration;
        });
        return builder;
    }

    /// <summary>
    /// True for the ways a call to another service fails without that service saying anything
    /// useful: no connection, a timeout, or a circuit that is open. Callers turn these into
    /// "unavailable" instead of an internal error.
    /// </summary>
    public static bool IsUnavailable(Exception exception, CancellationToken requestAborted) =>
        exception is HttpRequestException or Polly.ExecutionRejectedException
        || (exception is TaskCanceledException && !requestAborted.IsCancellationRequested);
}
