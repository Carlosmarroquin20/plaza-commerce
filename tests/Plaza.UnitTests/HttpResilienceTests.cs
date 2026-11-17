using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Plaza.ServiceDefaults;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Xunit;

namespace Plaza.UnitTests;

/// <summary>The resilience pipeline of ADR 0022, exercised against a server that misbehaves on purpose.</summary>
public sealed class HttpResilienceTests
{
    private static readonly HttpResilience.Settings Fast = new()
    {
        AttemptTimeout = TimeSpan.FromMilliseconds(250),
        TotalTimeout = TimeSpan.FromSeconds(5),
        Retries = 2,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        BreakerSamplingDuration = TimeSpan.FromSeconds(2),
        BreakerMinimumThroughput = 4,
        BreakerFailureRatio = 0.5,
        BreakerBreakDuration = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public async Task Read_that_fails_is_tried_again_until_it_works()
    {
        var server = new Server((_, attempt) => attempt < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        using var client = ClientFor(server);

        var response = await client.GetAsync("http://plaza.test/products", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, server.Calls);
    }

    [Fact]
    public async Task Read_gives_up_after_the_configured_retries()
    {
        var server = new Server((_, _) => HttpStatusCode.ServiceUnavailable);
        using var client = ClientFor(server);

        var response = await client.GetAsync("http://plaza.test/products", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, server.Calls); // the first attempt and two retries
    }

    // Repeating a POST can repeat its effect: an order placed twice, a message sent twice.
    [Fact]
    public async Task Write_that_fails_is_not_repeated()
    {
        var server = new Server((_, _) => HttpStatusCode.ServiceUnavailable);
        using var client = ClientFor(server);

        var response = await client.PostAsync("http://plaza.test/orders", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task Request_the_caller_got_wrong_is_not_retried()
    {
        var server = new Server((_, _) => HttpStatusCode.BadRequest);
        using var client = ClientFor(server);

        var response = await client.GetAsync("http://plaza.test/products", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, server.Calls);
    }

    [Fact]
    public async Task Circuit_opens_after_repeated_failures_and_stops_calling_the_server()
    {
        var server = new Server((_, _) => HttpStatusCode.ServiceUnavailable);
        using var client = ClientFor(server);

        // Enough failed calls to fill the breaker's sample.
        for (var i = 0; i < Fast.BreakerMinimumThroughput; i++)
        {
            await client.PostAsync("http://plaza.test/orders", content: null, TestContext.Current.CancellationToken);
        }

        var callsWhenOpened = server.Calls;
        var watch = Stopwatch.StartNew();
        var rejection = await Assert.ThrowsAsync<BrokenCircuitException>(() =>
            client.PostAsync("http://plaza.test/orders", content: null, TestContext.Current.CancellationToken));

        // Refused at once, without touching a server that is already in trouble.
        Assert.Equal(callsWhenOpened, server.Calls);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(200), $"took {watch.Elapsed}");
        Assert.True(HttpResilience.IsUnavailable(rejection, CancellationToken.None));
    }

    [Fact]
    public async Task Healthy_server_keeps_the_circuit_closed()
    {
        var server = new Server((_, _) => HttpStatusCode.OK);
        using var client = ClientFor(server);

        for (var i = 0; i < 20; i++)
        {
            var response = await client.PostAsync("http://plaza.test/orders", content: null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(20, server.Calls);
    }

    [Fact]
    public async Task Attempt_that_takes_too_long_is_abandoned()
    {
        var server = new Server((_, _) => HttpStatusCode.OK, delay: TimeSpan.FromSeconds(10));
        using var client = ClientFor(server);

        var watch = Stopwatch.StartNew();
        var timeout = await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
            client.PostAsync("http://plaza.test/orders", content: null, TestContext.Current.CancellationToken));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.True(HttpResilience.IsUnavailable(timeout, CancellationToken.None));
    }

    [Fact]
    public void Only_failures_to_reach_a_service_count_as_unavailable()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        Assert.True(HttpResilience.IsUnavailable(new HttpRequestException("refused"), CancellationToken.None));
        Assert.True(HttpResilience.IsUnavailable(new TaskCanceledException("timed out"), CancellationToken.None));
        // The client went away: that is not the other service's fault.
        Assert.False(HttpResilience.IsUnavailable(new TaskCanceledException("client left"), aborted.Token));
        Assert.False(HttpResilience.IsUnavailable(new InvalidOperationException("bug"), CancellationToken.None));
    }

    private static HttpClient ClientFor(Server server)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("probe")
            .ConfigurePrimaryHttpMessageHandler(() => server)
            .AddPlazaResilience(Fast);
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("probe");
    }

    /// <summary>Answers each request with what the test decides, counting the calls it receives.</summary>
    private sealed class Server(Func<HttpRequestMessage, int, HttpStatusCode> respond, TimeSpan delay = default) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _calls);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return new HttpResponseMessage(respond(request, attempt));
        }
    }
}
