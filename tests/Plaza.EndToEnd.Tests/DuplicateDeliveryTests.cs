using Plaza.Contracts;
using Plaza.Messaging;
using Plaza.Orders;
using Plaza.PaymentProvider;
using Plaza.Testing;
using Xunit;

namespace Plaza.EndToEnd.Tests;

/// <summary>
/// Runs the checkout with every service publishing every message twice: the worst case of
/// at-least-once delivery. Outcomes must be identical to the single-delivery case.
/// </summary>
public sealed class DuplicateDeliveryTests(DuplicatingPlazaSystem plaza) : IClassFixture<DuplicatingPlazaSystem>
{
    [Fact]
    public async Task Every_message_delivered_twice_still_charges_once_and_reserves_once()
    {
        const int paidOrders = 8;
        await plaza.SetStockAsync(SeedProducts.Monitor24, 20);
        await plaza.SetStockAsync(SeedProducts.MonitorArm, 20);
        await plaza.SetStockAsync(SeedProducts.SlimKeyboard, 0);
        await plaza.SetStockAsync(SeedProducts.MechanicalKeyboard, 5);

        var paid = await Task.WhenAll(Enumerable.Range(0, paidOrders).Select(_ => plaza.PlaceOrderAsync(
            "tok_test_visa", new PlaceOrderLine(SeedProducts.Monitor24, 2), new PlaceOrderLine(SeedProducts.MonitorArm, 1))));
        var declined = await plaza.PlaceOrderAsync(
            ChargeSimulator.DeclinedToken, new PlaceOrderLine(SeedProducts.MechanicalKeyboard, 1));
        var outOfStock = await plaza.PlaceOrderAsync(
            "tok_test_visa", new PlaceOrderLine(SeedProducts.SlimKeyboard, 1));

        foreach (var placed in paid)
        {
            var order = await plaza.WaitForFinalStatusAsync(placed.Id);
            Assert.Equal("Confirmed", order.Status);

            var payment = Assert.Single(await plaza.GetPaymentsAsync(order.Id));
            Assert.Equal(("Succeeded", order.TotalCents), (payment.Status, payment.AmountCents));

            // The provider's ledger agrees: one charge per order, asked for more than once.
            var charge = Assert.Single(await plaza.GetProviderChargesAsync(order.Id));
            Assert.Equal(("Succeeded", order.TotalCents), (charge.Status, charge.AmountCents));
        }

        Assert.Equal("PaymentFailed", (await plaza.WaitForFinalStatusAsync(declined.Id)).Status);
        Assert.Single(await plaza.GetPaymentsAsync(declined.Id));

        Assert.Equal("Rejected", (await plaza.WaitForFinalStatusAsync(outOfStock.Id)).Status);
        Assert.Empty(await plaza.GetPaymentsAsync(outOfStock.Id));

        // Stock moved exactly once per order: 20 - 8 x 2 and 20 - 8 x 1, nothing left reserved.
        // The declined order's unit came back once, not twice.
        await Eventually.UntilAsync(
            async () => (await plaza.GetStockAsync(SeedProducts.Monitor24)) is { Available: 4, Reserved: 0 }
                && (await plaza.GetStockAsync(SeedProducts.MonitorArm)) is { Available: 12, Reserved: 0 }
                && (await plaza.GetStockAsync(SeedProducts.MechanicalKeyboard)) is { Available: 5, Reserved: 0 },
            "stock to be decremented exactly once per order");

        foreach (var placed in paid)
        {
            await Eventually.UntilAsync(
                async () => (await plaza.GetNotificationsAsync(placed.Id)).Count == 1,
                $"exactly one notification for order {placed.Id}");
        }

        // Guard against a vacuous pass: prove the broker really holds every message twice.
        // 10 orders placed, 9 payment requests (all but the out-of-stock one), 8 confirmations
        // and 1 cancellation (the declined order).
        const int distinctOrderMessages = 10 + 9 + 8 + 1;
        var onTopic = await KafkaTestClient.ReadUntilAsync(
            plaza.KafkaBootstrapServers,
            Topics.Orders,
            messages => messages.Count >= distinctOrderMessages * 2,
            Eventually.DefaultTimeout);
        var copiesPerMessage = onTopic.GroupBy(m => m.Headers[MessageHeaders.MessageId]).ToList();
        Assert.Equal(distinctOrderMessages, copiesPerMessage.Count);
        Assert.All(copiesPerMessage, copies => Assert.Equal(2, copies.Count()));
    }
}
