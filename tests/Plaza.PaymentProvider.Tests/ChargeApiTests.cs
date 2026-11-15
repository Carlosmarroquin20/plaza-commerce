using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Plaza.PaymentProvider.Data;
using Plaza.Testing;
using Xunit;

namespace Plaza.PaymentProvider.Tests;

/// <summary>The provider running in this process against a real PostgreSQL.</summary>
public sealed class ProviderApi : IAsyncLifetime
{
    private readonly InfrastructureFixture _containers = new(withKafka: false);
    private WebApplicationFactory<ProviderDbContext>? _factory;

    public HttpClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _containers.StartAsync();
        _factory = new WebApplicationFactory<ProviderDbContext>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:payment-provider", _containers.ConnectionString("payment_provider"));
        });
        Client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _containers.DisposeAsync();
    }
}

public sealed class ChargeApiTests(ProviderApi api) : IClassFixture<ProviderApi>
{
    private static readonly ChargeRequest Visa = new("tok_test_visa", 35300, "USD", "order-1");

    [Fact]
    public async Task Charge_without_idempotency_key_is_refused()
    {
        var response = await api.Client.PostAsJsonAsync("/charges", Visa, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("")]
    public async Task Malformed_idempotency_key_is_refused(string key)
    {
        var response = await ChargeAsync(key, Visa);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null, "USD")]
    [InlineData("tok_test_visa", "usd")]
    [InlineData("tok_test_visa", null)]
    public async Task Malformed_request_is_refused_and_does_not_spend_the_key(string? token, string? currency)
    {
        var key = NewKey();

        var response = await ChargeAsync(key, new ChargeRequest(token, 100, currency, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client.GetAsync($"/charges/by-key/{key}", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Repeating_a_charge_returns_the_first_outcome_and_moves_no_more_money()
    {
        var key = NewKey();
        var request = Visa with { Reference = NewKey() };

        var first = await ChargeAsync(key, request);
        var second = await ChargeAsync(key, request);

        var original = await ReadAsync(first);
        var replay = await ReadAsync(second);
        Assert.Equal("Succeeded", original.Status);
        Assert.False(first.Headers.Contains(ChargeEndpoints.ReplayedHeader));
        Assert.True(second.Headers.Contains(ChargeEndpoints.ReplayedHeader));
        Assert.Equal(original.Id, replay.Id);
        Assert.Equal((1, 2), (original.Attempts, replay.Attempts));
        Assert.Single(await ListAsync(request.Reference!));
    }

    [Fact]
    public async Task Concurrent_requests_with_one_key_create_one_charge()
    {
        var key = NewKey();
        var request = Visa with { Reference = NewKey() };

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => ChargeAsync(key, request))));
        var charges = await Task.WhenAll(responses.Select(ReadAsync));

        Assert.Single(charges.Select(c => c.Id).Distinct());
        var stored = Assert.Single(await ListAsync(request.Reference!));
        Assert.Equal(("Succeeded", 20), (stored.Status, stored.Attempts));
    }

    [Fact]
    public async Task Key_reused_for_a_different_request_is_rejected()
    {
        var key = NewKey();
        await ChargeAsync(key, Visa);

        var response = await ChargeAsync(key, Visa with { AmountCents = 1 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(35300, (await GetAsync(key)).AmountCents);
    }

    [Fact]
    public async Task Declined_charge_is_replayed_as_declined()
    {
        var key = NewKey();
        var request = Visa with { Token = ChargeSimulator.DeclinedToken };

        var first = await ReadAsync(await ChargeAsync(key, request));
        var second = await ReadAsync(await ChargeAsync(key, request));

        Assert.Equal(("Declined", "card_declined"), (first.Status, first.DeclineReason));
        Assert.Equal(("Declined", "card_declined"), (second.Status, second.DeclineReason));
    }

    [Fact]
    public async Task Void_refunds_a_successful_charge_once()
    {
        var key = NewKey();
        await ChargeAsync(key, Visa);

        var first = await VoidAsync(key);
        var second = await VoidAsync(key);

        Assert.Equal("Refunded", first.Status);
        Assert.Equal("Refunded", second.Status);
        // A repeated charge request must not turn the refund back into a charge.
        Assert.Equal("Refunded", (await ReadAsync(await ChargeAsync(key, Visa))).Status);
    }

    [Fact]
    public async Task Void_before_the_charge_makes_the_charge_fail()
    {
        var key = NewKey();

        Assert.Equal("Voided", (await VoidAsync(key)).Status);
        var charge = await ReadAsync(await ChargeAsync(key, Visa));

        Assert.Equal(("Voided", 0L), (charge.Status, charge.AmountCents));
    }

    [Fact]
    public async Task Void_leaves_a_declined_charge_as_it_is()
    {
        var key = NewKey();
        await ChargeAsync(key, Visa with { Token = ChargeSimulator.InsufficientFundsToken });

        Assert.Equal("Declined", (await VoidAsync(key)).Status);
    }

    [Fact]
    public async Task Charges_are_listed_by_reference_across_keys()
    {
        var reference = NewKey();
        await ChargeAsync(NewKey(), Visa with { Reference = reference });
        await ChargeAsync(NewKey(), Visa with { Reference = reference });

        Assert.Equal(2, (await ListAsync(reference)).Count);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await api.Client.GetAsync("/charges", TestContext.Current.CancellationToken)).StatusCode);
    }

    private static string NewKey() => $"test-{Guid.NewGuid():N}";

    private async Task<HttpResponseMessage> ChargeAsync(string key, ChargeRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/charges") { Content = JsonContent.Create(request) };
        message.Headers.TryAddWithoutValidation(ChargeEndpoints.IdempotencyKeyHeader, key);
        return await api.Client.SendAsync(message);
    }

    private async Task<ChargeResponse> VoidAsync(string key)
    {
        var response = await api.Client.PutAsync($"/charges/by-key/{key}/void", content: null);
        return await ReadAsync(response);
    }

    private async Task<ChargeResponse> GetAsync(string key) =>
        (await api.Client.GetFromJsonAsync<ChargeResponse>($"/charges/by-key/{key}"))!;

    private async Task<List<ChargeResponse>> ListAsync(string reference) =>
        (await api.Client.GetFromJsonAsync<List<ChargeResponse>>($"/charges?reference={reference}"))!;

    private static async Task<ChargeResponse> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChargeResponse>())!;
    }
}
