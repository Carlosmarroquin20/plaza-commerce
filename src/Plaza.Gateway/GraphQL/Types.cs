using HotChocolate.CostAnalysis.Types;
using Plaza.Gateway.Clients;

namespace Plaza.Gateway.GraphQL;

// The public schema (ADR 0017). Types are plain records filled from the internal REST APIs;
// fields that need another service are resolved through DataLoaders, so a list of N products
// costs one call to that service, not N.

/// <summary>A product of the catalogue.</summary>
public sealed record Product(Guid Id, string Sku, string Name, string Description, string Category, long PriceCents, string Currency)
{
    public static Product From(ProductDto dto) =>
        new(dto.Id, dto.Sku, dto.Name, dto.Description, dto.Category, dto.PriceCents, dto.Currency);
}

[ExtendObjectType<Product>]
public sealed class ProductResolvers
{
    /// <summary>Units that can be bought right now.</summary>
    [Cost(1)] // Batched: every product in the request shares one call to Inventory.
    public async Task<int> GetAvailableStockAsync(
        [Parent] Product product, StockByProductIdDataLoader stock, CancellationToken cancellationToken) =>
        await stock.LoadAsync(product.Id, cancellationToken);

    /// <summary>
    /// Products similar to this one. Each product on which this field is selected costs one
    /// search, which is why the field is expensive and the depth of a query is limited.
    /// </summary>
    [Cost(25)]
    [ListSize(AssumedSize = 3, SlicingArguments = ["first"], RequireOneSlicingArgument = false)]
    public async Task<IReadOnlyList<Product>> GetSimilarAsync(
        [Parent] Product product, CatalogClient catalog, CancellationToken cancellationToken, int first = 3)
    {
        var hits = await catalog.SearchAsync(product.Name, Math.Clamp(first, 1, Limits.MaxSimilar) + 1, null, cancellationToken);
        return [.. hits.Where(h => h.Id != product.Id).Take(Math.Clamp(first, 1, Limits.MaxSimilar)).Select(Product.From)];
    }
}

/// <summary>One page of the catalogue.</summary>
public sealed record ProductPage(IReadOnlyList<Product> Items, int Page, int PageSize, int Total);

/// <summary>A line of an order, with the name and price the product had when it was bought.</summary>
public sealed record OrderItem(Guid ProductId, string ProductName, long UnitPriceCents, int Quantity);

[ExtendObjectType<OrderItem>]
public sealed class OrderItemResolvers
{
    /// <summary>The product as it is in the catalogue today; null if it no longer exists.</summary>
    [Cost(1)] // Batched: every line in the request shares one call to Catalog.
    public async Task<Product?> GetProductAsync(
        [Parent] OrderItem item, ProductByIdDataLoader products, CancellationToken cancellationToken) =>
        await products.LoadAsync(item.ProductId, cancellationToken);
}

/// <summary>An order and where its checkout stands.</summary>
public sealed record Order(
    Guid Id,
    string Status,
    long TotalCents,
    string Currency,
    string? FailureReason,
    [property: ListSize(AssumedSize = 10)] IReadOnlyList<OrderItem> Items,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt)
{
    public static Order From(OrderDto dto) => new(
        dto.Id,
        dto.Status,
        dto.TotalCents,
        dto.Currency,
        dto.FailureReason,
        [.. dto.Items.Select(i => new OrderItem(i.ProductId, i.ProductName, i.UnitPriceCents, i.Quantity))],
        dto.CreatedAt,
        dto.UpdatedAt,
        dto.ExpiresAt);
}

public sealed record OrderLineInput(Guid ProductId, int Quantity);

/// <param name="PaymentToken">Opaque token from the payment provider (tok_...). Never card data.</param>
public sealed record PlaceOrderInput(string PaymentToken, IReadOnlyList<OrderLineInput> Lines);

/// <summary>The accepted order, or the reasons it was refused. An accepted order is still being checked out.</summary>
public sealed record PlaceOrderPayload(Order? Order, IReadOnlyList<string> Errors);

public sealed record CartLine(Guid ProductId, string Name, long UnitPriceCents, int Quantity, long LineTotalCents);

/// <summary>A proposal built by the assistant; nothing is reserved or charged.</summary>
public sealed record Cart(IReadOnlyList<CartLine> Lines, long TotalCents, string Currency, IReadOnlyList<string> Problems);

public sealed record AskAssistantPayload(string? Reply, Cart? Cart, bool Blocked, string? Error);

public static class Limits
{
    public const int MaxPageSize = 50;
    public const int MaxSearchResults = 20;
    public const int MaxSimilar = 5;
    public const int MaxOrders = 50;
    public const int MaxOrderLines = 50;
    public const int MaxMessageLength = 2000;
}

/// <summary>Stock for every product selected in a request, fetched in one call to Inventory.</summary>
public sealed class StockByProductIdDataLoader(InventoryClient inventory, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, int>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<Guid, int>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var available = await inventory.GetAvailableAsync(keys, cancellationToken);

        // A product without a stock record has nothing to sell.
        return keys.ToDictionary(key => key, key => available.GetValueOrDefault(key));
    }
}

/// <summary>Products for every order line selected in a request, fetched in one call to Catalog.</summary>
public sealed class ProductByIdDataLoader(CatalogClient catalog, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, Product?>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<Guid, Product?>> LoadBatchAsync(
        IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var products = await catalog.GetManyAsync(keys, cancellationToken);
        return keys.ToDictionary(key => key, key => products.TryGetValue(key, out var dto) ? Product.From(dto) : null);
    }
}
