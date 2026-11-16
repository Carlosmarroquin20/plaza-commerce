using System.ComponentModel;
using Microsoft.Extensions.AI;
using Plaza.Shopping;

namespace Plaza.Assistant;

/// <summary>
/// The tools the model can call during one turn, and what the turn learned from them. The
/// tools only read and compute: there is none that places an order, takes a payment or
/// changes stock, so a model that is talked into misbehaving has nothing harmful to call
/// (ADR 0014).
/// </summary>
public sealed class TurnTools(ShoppingService shopping)
{
    /// <summary>The cart built in this turn, taken from the tool's own result, never from the model's text.</summary>
    public Cart? Cart { get; private set; }

    /// <summary>How many catalogue texts were withheld from the model because they read like instructions.</summary>
    public int NeutralizedTexts { get; private set; }

    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(SearchProductsAsync, "search_products"),
        AIFunctionFactory.Create(CheckStockAsync, "check_stock"),
        AIFunctionFactory.Create(BuildCartAsync, "build_cart"),
    ];

    [Description("Searches the Plaza catalogue. Returns the best matching products with their id, name, description, category and price. Call it once per kind of product the customer needs.")]
    public async Task<IReadOnlyList<ProductSummary>> SearchProductsAsync(
        [Description("What to look for, in the customer's words or as keywords, for example 'quiet keyboard'.")] string query,
        [Description("How many products to return, 1 to 10.")] int maxResults = 5,
        [Description("Only return products that cost at most this much, in the catalogue's currency.")] decimal? maxPrice = null,
        CancellationToken cancellationToken = default)
    {
        var products = await shopping.SearchProductsAsync(query, maxResults, maxPrice, cancellationToken);

        // Names and descriptions are written by whoever maintains the catalogue: untrusted.
        return [.. products.Select(p => p with
        {
            Name = UntrustedText.Neutralize(p.Name, () => NeutralizedTexts++),
            Description = UntrustedText.Neutralize(p.Description, () => NeutralizedTexts++),
        })];
    }

    [Description("Returns how many units of each product can be bought right now.")]
    public Task<IReadOnlyList<StockLevel>> CheckStockAsync(
        [Description("Ids of the products, as returned by search_products.")] Guid[] productIds,
        CancellationToken cancellationToken = default) =>
        shopping.CheckStockAsync(productIds ?? [], cancellationToken);

    [Description("Builds the customer's cart from product ids and quantities, replacing any earlier cart. Prices and stock are checked by the store; items that cannot be added are listed under 'problems'. This does not place an order.")]
    public async Task<Cart> BuildCartAsync(
        [Description("The products to put in the cart.")] CartItemRequest[] items,
        CancellationToken cancellationToken = default)
    {
        var cart = await shopping.BuildCartAsync(items ?? [], cancellationToken);
        Cart = cart with
        {
            Lines = [.. cart.Lines.Select(l => l with { Name = UntrustedText.Neutralize(l.Name) })],
            Problems = [.. cart.Problems.Select(p => UntrustedText.Neutralize(p))],
        };
        return Cart;
    }
}
