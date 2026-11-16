using System.Net;
using System.Net.Http.Json;
using Plaza.Contracts;
using Plaza.Orders;
using Plaza.Testing;
using Xunit;

namespace Plaza.EndToEnd.Tests;

/// <summary>
/// The system tuned to fail fast: a short provider timeout and short retry delays, so that
/// faults are met and recovered from within a test. The broker can be stopped and started.
/// </summary>
public class ChaosPlazaSystem : PlazaSystem
{
    public ChaosPlazaSystem()
        : base(new InfrastructureFixture(restartableKafka: true))
    {
    }

    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["PaymentProvider:Timeout"] = "00:00:01",
        ["Messaging:RetryBaseDelay"] = "00:00:00.050",
        ["Messaging:RetryMaxDelay"] = "00:00:00.200",
    };

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await WarmUpAsync();
    }

    public Task StopKafkaAsync() => Containers.StopKafkaAsync();

    public Task StartKafkaAsync() => Containers.StartKafkaAsync();
}

/// <summary>
/// Things that go wrong around a checkout that must still complete: the provider answers late,
/// the provider is down for a moment, the broker restarts. In every case each order ends
/// confirmed and charged exactly once.
/// </summary>
public sealed class ChaosTests : IClassFixture<ChaosPlazaSystem>
{
    private const string GoodToken = "tok_test_visa";

    private readonly ChaosPlazaSystem _plaza;

    public ChaosTests(ChaosPlazaSystem plaza)
    {
        _plaza = plaza;
        _plaza.Chaos.Reset();
    }

    [Fact]
    public async Task Slow_provider_is_retried_with_the_same_key_and_charges_once()
    {
        await _plaza.SetStockAsync(SeedProducts.StandingDesk, 10);
        // Twice the provider takes the money but answers too late; the third request is answered.
        _plaza.Chaos.FirstAttempts(Fault.AnswerLost, 2);

        var placed = await _plaza.PlaceOrderAsync(GoodToken, new PlaceOrderLine(SeedProducts.StandingDesk, 1));
        var order = await _plaza.WaitForFinalStatusAsync(placed.Id);

        Assert.Equal("Confirmed", order.Status);
        // Three requests reached the provider; they produced one charge.
        var charge = Assert.Single(await _plaza.GetProviderChargesAsync(order.Id));
        Assert.Equal(("Succeeded", order.TotalCents, 3), (charge.Status, charge.AmountCents, charge.Attempts));
        Assert.Equal(2, _plaza.Chaos.AnswersLost);
        Assert.Single(await _plaza.GetPaymentsAsync(order.Id));
    }

    [Fact]
    public async Task Provider_outage_shorter_than_the_retry_budget_only_delays_the_order()
    {
        await _plaza.SetStockAsync(SeedProducts.CompactDesk, 10);
        _plaza.Chaos.FirstAttempts(Fault.Unavailable, 3);

        var placed = await _plaza.PlaceOrderAsync(GoodToken, new PlaceOrderLine(SeedProducts.CompactDesk, 1));
        var order = await _plaza.WaitForFinalStatusAsync(placed.Id);

        Assert.Equal("Confirmed", order.Status);
        var charge = Assert.Single(await _plaza.GetProviderChargesAsync(order.Id));
        Assert.Equal(("Succeeded", 1), (charge.Status, charge.Attempts));
        Assert.Equal(3, _plaza.Chaos.Unavailable);
    }

    [Fact]
    public async Task Unreliable_provider_never_loses_an_order_or_charges_it_twice()
    {
        const int orders = 30;
        const int stock = 100;
        await _plaza.SetStockAsync(SeedProducts.ErgonomicChair, stock);

        // A third of the charges go through at once, a third fail once and a third fail twice,
        // alternating between a provider that is down and one that charges without answering.
        _plaza.Chaos.Plan((ordinal, attempt) =>
            attempt >= ordinal % 3 ? Fault.None
            : (ordinal + attempt) % 2 == 0 ? Fault.Unavailable
            : Fault.AnswerLost);

        var placed = await Task.WhenAll(Enumerable.Range(0, orders).Select(_ =>
            _plaza.PlaceOrderAsync(GoodToken, new PlaceOrderLine(SeedProducts.ErgonomicChair, 1))));
        var final = await Task.WhenAll(placed.Select(o => _plaza.WaitForFinalStatusAsync(o.Id)));

        Assert.All(final, order => Assert.Equal("Confirmed", order.Status));
        await _plaza.AssertMoneyAndStockAreConsistentAsync(final, SeedProducts.ErgonomicChair, stock, quantityPerOrder: 1);

        // Guard against a vacuous pass: the faults really happened. 10 charges failed once and
        // 10 failed twice: 15 requests never reached the provider and 15 were processed by it
        // without the caller knowing, on top of the 30 that were answered.
        Assert.Equal((15, 15), (_plaza.Chaos.Unavailable, _plaza.Chaos.AnswersLost));
        var charges = (await Task.WhenAll(final.Select(o => _plaza.GetProviderChargesAsync(o.Id)))).SelectMany(c => c).ToList();
        Assert.Equal(orders, charges.Count);
        Assert.True(charges.Sum(c => c.Attempts) >= orders + 15, "the provider must have seen the repeated requests");
    }

    [Fact]
    public async Task Broker_restart_in_the_middle_of_checkouts_loses_no_order()
    {
        const int beforeOutage = 10;
        const int duringOutage = 10;
        const int stock = 100;
        await _plaza.SetStockAsync(SeedProducts.Monitor24, stock);
        // A slow provider keeps the first orders in the middle of their checkout long enough
        // for the broker to go down under them.
        _plaza.Chaos.FirstAttempts(Fault.AnswerLost, 2);

        var first = await Task.WhenAll(Enumerable.Range(0, beforeOutage).Select(_ =>
            _plaza.PlaceOrderAsync(GoodToken, new PlaceOrderLine(SeedProducts.Monitor24, 1))));

        await _plaza.StopKafkaAsync();

        var inFlight = (await Task.WhenAll(first.Select(o => _plaza.GetOrderAsync(o.Id))))
            .Count(o => !PlazaSystem.FinalStatuses.Contains(o.Status));
        Assert.True(inFlight > 0, "the broker must go down while checkouts are in progress");

        // The API does not need the broker: orders are accepted into the outbox.
        var responses = await Task.WhenAll(Enumerable.Range(0, duringOutage).Select(_ =>
            _plaza.PostOrderAsync(GoodToken, new PlaceOrderLine(SeedProducts.Monitor24, 1))));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        var second = await Task.WhenAll(responses.Select(async response =>
            (await response.Content.ReadFromJsonAsync<OrderResponse>())!));

        // Nothing can move without the broker: these orders wait where they are.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.All(
            await Task.WhenAll(second.Select(o => _plaza.GetOrderAsync(o.Id))),
            order => Assert.Equal("Pending", order.Status));

        await _plaza.StartKafkaAsync();

        var final = await Task.WhenAll(first.Concat(second).Select(o =>
            _plaza.WaitForFinalStatusAsync(o.Id, TimeSpan.FromMinutes(3))));

        Assert.All(final, order => Assert.Equal("Confirmed", order.Status));
        await _plaza.AssertMoneyAndStockAreConsistentAsync(final, SeedProducts.Monitor24, stock, quantityPerOrder: 1);
        foreach (var order in final)
        {
            Assert.Single(await _plaza.GetPaymentsAsync(order.Id));
            await Eventually.UntilAsync(
                async () => (await _plaza.GetNotificationsAsync(order.Id)).Count == 1,
                $"exactly one notification for order {order.Id}");
        }
    }
}

/// <summary>The fail-fast system with a checkout deadline of a few seconds.</summary>
public sealed class ExpiringPlazaSystem : PlazaSystem
{
    public static readonly TimeSpan ReservationTtl = TimeSpan.FromSeconds(6);

    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["Checkout:ReservationTtl"] = ReservationTtl.ToString(),
        ["Checkout:ExpirySweepInterval"] = "00:00:00.200",
        ["PaymentProvider:Timeout"] = "00:00:00.500",
        ["Messaging:MaxAttempts"] = "3",
        ["Messaging:RetryBaseDelay"] = "00:00:00.050",
        ["Messaging:RetryMaxDelay"] = "00:00:00.200",
    };

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // With a deadline this short, the start-up of the consumers alone could expire an order.
        await WarmUpAsync();
    }
}

/// <summary>
/// Checkouts that cannot complete: the saga gives up at the deadline and undoes what was done.
/// </summary>
public sealed class ExpiryTests : IClassFixture<ExpiringPlazaSystem>
{
    private const string GoodToken = "tok_test_visa";

    private readonly ExpiringPlazaSystem _plaza;

    public ExpiryTests(ExpiringPlazaSystem plaza)
    {
        _plaza = plaza;
        _plaza.Chaos.Reset();
    }

    [Fact]
    public async Task Provider_down_past_the_retry_budget_expires_the_order_and_releases_its_stock()
    {
        await _plaza.SetStockAsync(SeedProducts.TaskChair, 10);
        _plaza.Chaos.Always(Fault.Unavailable);

        var placed = await _plaza.PlaceOrderAsync(GoodToken, new PlaceOrderLine(SeedProducts.TaskChair, 2));
        await Eventually.UntilAsync(
            async () => (await _plaza.GetStockAsync(SeedProducts.TaskChair)) is { Available: 8, Reserved: 2 },
            "the stock to be reserved while the payment is attempted");
        var order = await _plaza.WaitForFinalStatusAsync(placed.Id);

        Assert.Equal(("Expired", "expired"), (order.Status, order.FailureReason));
        Assert.True(order.UpdatedAt >= order.ExpiresAt, "the order must not expire before its deadline");
        await _plaza.AssertMoneyAndStockAreConsistentAsync([order], SeedProducts.TaskChair, initialStock: 10, quantityPerOrder: 2);
        Assert.Empty(await _plaza.GetNotificationsAsync(order.Id));

        // The payment request was given up on and parked, not dropped.
        var deadLetters = await KafkaTestClient.ReadUntilAsync(
            _plaza.KafkaBootstrapServers,
            $"{Topics.Orders}.dlt",
            messages => messages.Any(m => m.Key == order.Id.ToString()),
            Eventually.DefaultTimeout);
        var parked = Assert.Single(deadLetters, m => m.Key == order.Id.ToString());
        Assert.Equal("orders.payment-requested.v1", parked.Headers[Messaging.MessageHeaders.MessageType]);
    }

    [Fact]
    public async Task Charge_taken_without_an_answer_is_refunded_when_the_order_expires()
    {
        await _plaza.SetStockAsync(SeedProducts.Monitor27, 5);
        // The worst case for money: the provider charges every time it is asked, and Payments
        // never learns about it.
        _plaza.Chaos.Always(Fault.AnswerLost);

        var placed = await _plaza.PlaceOrderAsync(GoodToken, new PlaceOrderLine(SeedProducts.Monitor27, 1));
        await Eventually.UntilAsync(
            async () => (await _plaza.GetProviderChargesAsync(placed.Id)).Any(c => c.Status == "Succeeded"),
            "the provider to take the money");
        var order = await _plaza.WaitForFinalStatusAsync(placed.Id);

        Assert.Equal("Expired", order.Status);
        await _plaza.AssertMoneyAndStockAreConsistentAsync([order], SeedProducts.Monitor27, initialStock: 5, quantityPerOrder: 1);
        var charge = Assert.Single(await _plaza.GetProviderChargesAsync(order.Id));
        Assert.Equal(("Refunded", order.TotalCents), (charge.Status, charge.AmountCents));
        Assert.True(_plaza.Chaos.AnswersLost >= 1);
        // Payments never saw an answer, so it has no record; the provider's ledger is what counts.
        Assert.Empty(await _plaza.GetPaymentsAsync(order.Id));
    }

    [Fact]
    public async Task Order_that_completes_in_time_is_untouched_by_the_deadline()
    {
        await _plaza.SetStockAsync(SeedProducts.MonitorArm, 5);

        var placed = await _plaza.PlaceOrderAsync(GoodToken, new PlaceOrderLine(SeedProducts.MonitorArm, 1));
        var order = await _plaza.WaitForFinalStatusAsync(placed.Id);
        Assert.Equal("Confirmed", order.Status);

        await Task.Delay(ExpiringPlazaSystem.ReservationTtl + TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal("Confirmed", (await _plaza.WaitForFinalStatusAsync(order.Id)).Status);
        var charge = Assert.Single(await _plaza.GetProviderChargesAsync(order.Id));
        Assert.Equal("Succeeded", charge.Status);
        Assert.Equal(4, (await _plaza.GetStockAsync(SeedProducts.MonitorArm)).Available);
    }
}
