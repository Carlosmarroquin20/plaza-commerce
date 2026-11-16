using System.Globalization;

namespace Plaza.Shopping;

public sealed record ProductSummary(Guid Id, string Name, string Description, string Category, string Price, string Currency);

public sealed record StockLevel(Guid ProductId, int Available, bool InStock);

public sealed record CartItemRequest(Guid ProductId, int Quantity);

public sealed record CartLine(Guid ProductId, string Name, long UnitPriceCents, int Quantity, long LineTotalCents);

/// <summary>
/// A proposal, not an order: nothing is reserved or charged. <see cref="Problems"/> lists the
/// requested items that were left out and why.
/// </summary>
public sealed record Cart(IReadOnlyList<CartLine> Lines, long TotalCents, string Currency, IReadOnlyList<string> Problems)
{
    public static readonly Cart Empty = new([], 0, "USD", []);
}

/// <summary>
/// What a shopping assistant may do: look at the catalogue, look at stock, and put together a
/// cart. Every method is read-only and every limit is enforced here, whatever the caller (a
/// language model included) asked for.
/// </summary>
public sealed class ShoppingService(CatalogApi catalog, InventoryApi inventory)
{
    public const int MaxSearchResults = 10;
    public const int MaxQueryLength = 200;
    public const int MaxStockLookups = 20;
    public const int MaxCartLines = 20;
    public const int MaxQuantityPerProduct = 100;

    public async Task<IReadOnlyList<ProductSummary>> SearchProductsAsync(
        string query, int maxResults, decimal? maxPrice, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var text = query.Length <= MaxQueryLength ? query : query[..MaxQueryLength];
        long? maxPriceCents = maxPrice is > 0 and < 1_000_000 ? (long)(maxPrice.Value * 100) : null;
        var hits = await catalog.SearchAsync(text, Math.Clamp(maxResults, 1, MaxSearchResults), maxPriceCents, cancellationToken);

        return [.. hits.Select(h => new ProductSummary(h.Id, h.Name, h.Description, h.Category, FormatMoney(h.PriceCents), h.Currency))];
    }

    public async Task<IReadOnlyList<StockLevel>> CheckStockAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        var levels = new List<StockLevel>();
        foreach (var id in productIds.Distinct().Take(MaxStockLookups))
        {
            var available = await inventory.GetAvailableAsync(id, cancellationToken);
            levels.Add(new StockLevel(id, available, available > 0));
        }

        return levels;
    }

    /// <summary>
    /// Builds a cart from catalogue prices and current stock. Unknown products, quantities
    /// outside 1..<see cref="MaxQuantityPerProduct"/> and items without enough stock are left
    /// out and reported; prices never come from the caller.
    /// </summary>
    public async Task<Cart> BuildCartAsync(IReadOnlyCollection<CartItemRequest> items, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        if (items.Count > MaxCartLines)
        {
            problems.Add($"Only the first {MaxCartLines} items were considered.");
        }

        // Repeated products are merged, so one product is one line.
        var requested = items.Take(MaxCartLines)
            .GroupBy(i => i.ProductId)
            // Summed as long and clamped: quantities chosen to overflow must not wrap into a valid one.
            .Select(g => new CartItemRequest(g.Key, (int)Math.Clamp(g.Sum(i => (long)i.Quantity), int.MinValue, int.MaxValue)))
            .ToList();

        var products = await catalog.GetAsync([.. requested.Select(r => r.ProductId)], cancellationToken);
        var lines = new List<CartLine>();

        foreach (var item in requested)
        {
            if (!products.TryGetValue(item.ProductId, out var product))
            {
                problems.Add($"Product {item.ProductId:D} does not exist.");
                continue;
            }

            if (item.Quantity is < 1 or > MaxQuantityPerProduct)
            {
                problems.Add($"{product.Name}: the quantity must be between 1 and {MaxQuantityPerProduct}.");
                continue;
            }

            var available = await inventory.GetAvailableAsync(item.ProductId, cancellationToken);
            if (available < item.Quantity)
            {
                problems.Add($"{product.Name}: only {available} in stock, {item.Quantity} requested.");
                continue;
            }

            lines.Add(new CartLine(product.Id, product.Name, product.PriceCents, item.Quantity, product.PriceCents * item.Quantity));
        }

        var currency = products.Values.Select(p => p.Currency).FirstOrDefault() ?? "USD";
        return new Cart(lines, lines.Sum(l => l.LineTotalCents), currency, problems);
    }

    public static string FormatMoney(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
}
