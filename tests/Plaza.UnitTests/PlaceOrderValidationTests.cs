using Plaza.Orders;
using Xunit;

namespace Plaza.UnitTests;

public sealed class PlaceOrderValidationTests
{
    private static PlaceOrderRequest Valid() =>
        new(Guid.NewGuid(), "tok_test_ok", [new PlaceOrderLine(Guid.NewGuid(), 1)]);

    [Fact]
    public void Valid_request_has_no_errors() => Assert.Empty(OrderEndpoints.Validate(Valid()));

    [Fact]
    public void Empty_customer_is_rejected() =>
        Assert.Contains("customerId", OrderEndpoints.Validate(Valid() with { CustomerId = Guid.Empty }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tok_")]
    [InlineData("4242424242424242")] // a card number is not a token
    [InlineData("4242 4242 4242 4242")]
    [InlineData("tok_test_ok; DROP TABLE orders")]
    [InlineData("tok_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // too long
    public void Anything_that_is_not_token_shaped_is_rejected(string? token) =>
        Assert.Contains("paymentToken", OrderEndpoints.Validate(Valid() with { PaymentToken = token }));

    [Fact]
    public void Missing_or_empty_lines_are_rejected()
    {
        Assert.Contains("lines", OrderEndpoints.Validate(Valid() with { Lines = null }));
        Assert.Contains("lines", OrderEndpoints.Validate(Valid() with { Lines = [] }));
    }

    [Fact]
    public void Too_many_lines_are_rejected()
    {
        var lines = Enumerable.Range(0, OrderEndpoints.MaxLines + 1)
            .Select(_ => new PlaceOrderLine(Guid.NewGuid(), 1)).ToList();

        Assert.Contains("lines", OrderEndpoints.Validate(Valid() with { Lines = lines }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(OrderEndpoints.MaxQuantityPerProduct + 1)]
    public void Out_of_range_quantity_is_rejected(int quantity) =>
        Assert.Contains("lines", OrderEndpoints.Validate(
            Valid() with { Lines = [new PlaceOrderLine(Guid.NewGuid(), quantity)] }));

    [Fact]
    public void Empty_product_id_is_rejected() =>
        Assert.Contains("lines", OrderEndpoints.Validate(
            Valid() with { Lines = [new PlaceOrderLine(Guid.Empty, 1)] }));
}
