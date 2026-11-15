using System.Net;
using Plaza.Orders;
using Plaza.PaymentProvider;
using Plaza.Testing;
using Xunit;

namespace Plaza.EndToEnd.Tests;

public sealed class CheckoutFlowTests(PlazaSystem plaza) : IClassFixture<PlazaSystem>
{
    private const string GoodToken = "tok_test_visa";

    [Fact]
    public async Task Order_is_reserved_charged_once_confirmed_and_notified()
    {
        await plaza.SetStockAsync(SeedProducts.StandingDesk, 10);
        await plaza.SetStockAsync(SeedProducts.DeskLamp, 10);

        var placed = await plaza.PlaceOrderAsync(
            GoodToken, new PlaceOrderLine(SeedProducts.StandingDesk, 1), new PlaceOrderLine(SeedProducts.DeskLamp, 2));

        Assert.Equal("Pending", placed.Status);
        // Prices come from the catalogue: 289.00 + 2 x 32.00.
        Assert.Equal(35300, placed.TotalCents);

        var order = await plaza.WaitForFinalStatusAsync(placed.Id);
        Assert.Equal("Confirmed", order.Status);
        Assert.Null(order.FailureReason);

        var payment = Assert.Single(await plaza.GetPaymentsAsync(order.Id));
        Assert.Equal(("Succeeded", 35300L, "USD"), (payment.Status, payment.AmountCents, payment.Currency));

        // Inventory and Notifications react to OrderConfirmed after the order is final.
        await Eventually.UntilAsync(
            async () => (await plaza.GetStockAsync(SeedProducts.StandingDesk)) is { Available: 9, Reserved: 0 }
                && (await plaza.GetStockAsync(SeedProducts.DeskLamp)) is { Available: 8, Reserved: 0 },
            "stock to be decremented and the reservation committed");
        await Eventually.UntilAsync(
            async () => (await plaza.GetNotificationsAsync(order.Id)).Count == 1,
            "the customer to be notified once");
    }

    [Fact]
    public async Task Order_without_enough_stock_is_rejected_and_never_charged()
    {
        await plaza.SetStockAsync(SeedProducts.CompactDesk, 1);
        await plaza.SetStockAsync(SeedProducts.ErgonomicChair, 10);

        var placed = await plaza.PlaceOrderAsync(
            GoodToken, new PlaceOrderLine(SeedProducts.ErgonomicChair, 1), new PlaceOrderLine(SeedProducts.CompactDesk, 2));
        var order = await plaza.WaitForFinalStatusAsync(placed.Id);

        Assert.Equal(("Rejected", "insufficient_stock"), (order.Status, order.FailureReason));
        Assert.Empty(await plaza.GetPaymentsAsync(order.Id));
        Assert.Empty(await plaza.GetNotificationsAsync(order.Id));
        // All or nothing: the chair that was available is not held either.
        Assert.Equal(new { Available = 10, Reserved = 0 }, Shape(await plaza.GetStockAsync(SeedProducts.ErgonomicChair)));
        Assert.Equal(new { Available = 1, Reserved = 0 }, Shape(await plaza.GetStockAsync(SeedProducts.CompactDesk)));
    }

    [Fact]
    public async Task Declined_payment_fails_the_order_with_the_provider_reason()
    {
        await plaza.SetStockAsync(SeedProducts.TaskChair, 10);

        var placed = await plaza.PlaceOrderAsync(
            ChargeSimulator.DeclinedToken, new PlaceOrderLine(SeedProducts.TaskChair, 1));
        var order = await plaza.WaitForFinalStatusAsync(placed.Id);

        Assert.Equal(("PaymentFailed", "card_declined"), (order.Status, order.FailureReason));
        var payment = Assert.Single(await plaza.GetPaymentsAsync(order.Id));
        Assert.Equal("Failed", payment.Status);
        Assert.Empty(await plaza.GetNotificationsAsync(order.Id));
    }

    [Fact]
    public async Task Concurrent_orders_for_scarce_stock_confirm_exactly_what_exists_and_charge_only_those()
    {
        const int stock = 5;
        const int buyers = 25;
        await plaza.SetStockAsync(SeedProducts.Monitor27, stock);

        var placed = await Task.WhenAll(Enumerable.Range(0, buyers).Select(_ =>
            plaza.PlaceOrderAsync(GoodToken, new PlaceOrderLine(SeedProducts.Monitor27, 1))));
        var orders = await Task.WhenAll(placed.Select(o => plaza.WaitForFinalStatusAsync(o.Id)));

        Assert.Equal(stock, orders.Count(o => o.Status == "Confirmed"));
        Assert.Equal(buyers - stock, orders.Count(o => o.Status == "Rejected"));

        foreach (var order in orders)
        {
            var payments = await plaza.GetPaymentsAsync(order.Id);
            Assert.Equal(order.Status == "Confirmed" ? 1 : 0, payments.Count);
        }

        await Eventually.UntilAsync(
            async () => (await plaza.GetStockAsync(SeedProducts.Monitor27)) is { Available: 0, Reserved: 0 },
            "all stock to be sold and no reservation left open");
    }

    [Fact]
    public async Task Unknown_product_is_refused_before_anything_is_stored()
    {
        var response = await plaza.PostOrderAsync(GoodToken, new PlaceOrderLine(Guid.NewGuid(), 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("4242424242424242")]
    [InlineData("")]
    public async Task Card_numbers_and_malformed_tokens_are_refused(string token)
    {
        var response = await plaza.PostOrderAsync(token, new PlaceOrderLine(SeedProducts.MonitorArm, 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_order_is_not_found()
    {
        var response = await plaza.Orders.GetAsync($"/orders/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static object Shape(Inventory.StockResponse stock) => new { stock.Available, stock.Reserved };
}
