using System.Text.Json;
using Plaza.Contracts;
using Plaza.Messaging;
using Xunit;

namespace Plaza.UnitTests;

/// <summary>
/// There is no schema registry (ADR 0008), so these recorded samples are the contract. If one
/// of these tests fails, a published message type changed incompatibly: add a new version
/// instead of editing the sample.
/// </summary>
public sealed class ContractCompatibilityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string OrderId = "018f4e2a-7c3b-7d5e-9a1f-3b2c4d5e6f70";
    private const string OtherId = "a0000000-0000-4000-8000-000000000001";

    [Fact]
    public void Message_type_names_are_stable()
    {
        Assert.Equal("orders.order-placed.v1", Name<OrderPlaced>());
        Assert.Equal("orders.payment-requested.v1", Name<PaymentRequested>());
        Assert.Equal("orders.order-confirmed.v1", Name<OrderConfirmed>());
        Assert.Equal("orders.order-cancelled.v1", Name<OrderCancelled>());
        Assert.Equal("inventory.stock-reserved.v1", Name<StockReserved>());
        Assert.Equal("inventory.stock-rejected.v1", Name<StockRejected>());
        Assert.Equal("payments.payment-succeeded.v1", Name<PaymentSucceeded>());
        Assert.Equal("payments.payment-failed.v1", Name<PaymentFailed>());
    }

    [Fact]
    public void Topic_names_are_stable()
    {
        Assert.Equal("plaza.orders.v1", Topics.Orders);
        Assert.Equal("plaza.inventory.v1", Topics.Inventory);
        Assert.Equal("plaza.payments.v1", Topics.Payments);
    }

    [Fact]
    public void OrderPlaced_v1()
    {
        var message = Read<OrderPlaced>($$"""
            {"orderId":"{{OrderId}}","lines":[{"productId":"{{OtherId}}","quantity":2}]}
            """);

        Assert.Equal(Guid.Parse(OrderId), message.OrderId);
        var line = Assert.Single(message.Lines);
        Assert.Equal((Guid.Parse(OtherId), 2), (line.ProductId, line.Quantity));
    }

    [Fact]
    public void PaymentRequested_v1()
    {
        var message = Read<PaymentRequested>($$"""
            {"orderId":"{{OrderId}}","amountCents":35300,"currency":"USD","paymentToken":"tok_test_visa"}
            """);

        Assert.Equal(new PaymentRequested(Guid.Parse(OrderId), 35300, "USD", "tok_test_visa"), message);
    }

    [Fact]
    public void OrderConfirmed_v1()
    {
        var message = Read<OrderConfirmed>($$"""
            {"orderId":"{{OrderId}}","customerId":"{{OtherId}}","totalCents":35300,"currency":"USD"}
            """);

        Assert.Equal(new OrderConfirmed(Guid.Parse(OrderId), Guid.Parse(OtherId), 35300, "USD"), message);
    }

    [Fact]
    public void OrderCancelled_v1()
    {
        var message = Read<OrderCancelled>($$"""{"orderId":"{{OrderId}}","reason":"expired"}""");

        Assert.Equal(new OrderCancelled(Guid.Parse(OrderId), CancellationReasons.Expired), message);
        Assert.Equal("payment_failed", CancellationReasons.PaymentFailed);
    }

    [Fact]
    public void Inventory_replies_v1()
    {
        Assert.Equal(
            new StockReserved(Guid.Parse(OrderId)),
            Read<StockReserved>($$"""{"orderId":"{{OrderId}}"}"""));
        Assert.Equal(
            new StockRejected(Guid.Parse(OrderId), "insufficient_stock"),
            Read<StockRejected>($$"""{"orderId":"{{OrderId}}","reason":"insufficient_stock"}"""));
    }

    [Fact]
    public void Payment_replies_v1()
    {
        Assert.Equal(
            new PaymentSucceeded(Guid.Parse(OrderId), Guid.Parse(OtherId)),
            Read<PaymentSucceeded>($$"""{"orderId":"{{OrderId}}","paymentId":"{{OtherId}}"}"""));
        Assert.Equal(
            new PaymentFailed(Guid.Parse(OrderId), "card_declined"),
            Read<PaymentFailed>($$"""{"orderId":"{{OrderId}}","reason":"card_declined"}"""));
    }

    [Fact]
    public void Unknown_fields_are_tolerated_so_publishers_can_add_optional_data()
    {
        var message = Read<StockReserved>($$"""{"orderId":"{{OrderId}}","addedLater":true}""");

        Assert.Equal(Guid.Parse(OrderId), message.OrderId);
    }

    private static string Name<T>()
        where T : IMessage => T.MessageType;

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)!;
}
