using System.Globalization;
using System.Net;

namespace Plaza.Gateway.Clients;

// Typed clients for the internal REST APIs (ADR 0017). The gateway holds no data of its own:
// everything it serves comes from these calls.

public sealed record ProductDto(Guid Id, string Sku, string Name, string Description, string Category, long PriceCents, string Currency);

public sealed record ProductPageDto(IReadOnlyList<ProductDto> Items, int Page, int PageSize, int Total);

public sealed class CatalogClient(HttpClient http)
{
    public const int MaxBatchSize = 50;

    public async Task<ProductPageDto> ListAsync(int page, int pageSize, CancellationToken cancellationToken) =>
        await http.GetFromJsonAsync<ProductPageDto>(
            string.Create(CultureInfo.InvariantCulture, $"/products?page={page}&pageSize={pageSize}"), cancellationToken)
        ?? new ProductPageDto([], page, pageSize, 0);

    public async Task<IReadOnlyList<ProductDto>> SearchAsync(string query, int limit, long? maxPriceCents, CancellationToken cancellationToken)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"/products/search?q={Uri.EscapeDataString(query)}&limit={limit}");
        if (maxPriceCents is { } max)
        {
            url += string.Create(CultureInfo.InvariantCulture, $"&maxPriceCents={max}");
        }

        return await http.GetFromJsonAsync<List<ProductDto>>(url, cancellationToken) ?? [];
    }

    /// <summary>Products by id, in as many calls as the batch limit requires. Unknown ids are absent.</summary>
    public async Task<IReadOnlyDictionary<Guid, ProductDto>> GetManyAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var found = new Dictionary<Guid, ProductDto>();
        foreach (var chunk in ids.Chunk(MaxBatchSize))
        {
            var url = "/products/batch?" + string.Join('&', chunk.Select(id => $"ids={id:D}"));
            foreach (var product in await http.GetFromJsonAsync<List<ProductDto>>(url, cancellationToken) ?? [])
            {
                found[product.Id] = product;
            }
        }

        return found;
    }
}

public sealed class InventoryClient(HttpClient http)
{
    public const int MaxBatchSize = 100;

    private sealed record StockDto(Guid ProductId, int Available, int Reserved);

    /// <summary>Units available per product. Products Inventory does not know are absent.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> GetAvailableAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var available = new Dictionary<Guid, int>();
        foreach (var chunk in ids.Chunk(MaxBatchSize))
        {
            var url = "/stock/batch?" + string.Join('&', chunk.Select(id => $"ids={id:D}"));
            foreach (var stock in await http.GetFromJsonAsync<List<StockDto>>(url, cancellationToken) ?? [])
            {
                available[stock.ProductId] = stock.Available;
            }
        }

        return available;
    }
}

public sealed record OrderItemDto(Guid ProductId, string ProductName, long UnitPriceCents, int Quantity);

public sealed record OrderDto(
    Guid Id,
    Guid CustomerId,
    string Status,
    long TotalCents,
    string Currency,
    string? FailureReason,
    IReadOnlyList<OrderItemDto> Items,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt);

public sealed record PlaceOrderLineDto(Guid ProductId, int Quantity);

/// <summary>The outcome of asking Orders to accept an order: the order, or why it was refused.</summary>
public sealed record PlaceOrderResult(OrderDto? Order, IReadOnlyList<string> Errors);

public sealed class OrdersClient(HttpClient http)
{
    private sealed record PlaceOrderBody(Guid CustomerId, string PaymentToken, IReadOnlyList<PlaceOrderLineDto> Lines);

    private sealed record ValidationProblemDto(Dictionary<string, string[]>? Errors, string? Title);

    public async Task<OrderDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"/orders/{id:D}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OrderDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<OrderDto>> ListByCustomerAsync(Guid customerId, int limit, CancellationToken cancellationToken) =>
        await http.GetFromJsonAsync<List<OrderDto>>(
            string.Create(CultureInfo.InvariantCulture, $"/orders?customerId={customerId:D}&limit={limit}"), cancellationToken) ?? [];

    public async Task<PlaceOrderResult> PlaceAsync(
        Guid customerId, string paymentToken, IReadOnlyList<PlaceOrderLineDto> lines, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("/orders", new PlaceOrderBody(customerId, paymentToken, lines), cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // Orders explains what is wrong with the request; that is for the customer to read.
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDto>(cancellationToken);
            var errors = problem?.Errors?.SelectMany(e => e.Value).ToList() ?? [];
            return new PlaceOrderResult(null, errors.Count > 0 ? errors : [problem?.Title ?? "The order was refused."]);
        }

        response.EnsureSuccessStatusCode();
        return new PlaceOrderResult(await response.Content.ReadFromJsonAsync<OrderDto>(cancellationToken), []);
    }
}

public sealed record CartLineDto(Guid ProductId, string Name, long UnitPriceCents, int Quantity, long LineTotalCents);

public sealed record CartDto(IReadOnlyList<CartLineDto> Lines, long TotalCents, string Currency, IReadOnlyList<string> Problems);

public sealed record AssistantTurnDto(string Reply, CartDto? Cart, string? Blocked);

public sealed class AssistantClient(HttpClient http)
{
    private sealed record Body(string Text);

    /// <summary>Returns null when the conversation has reached its limit or the assistant refuses the request.</summary>
    public async Task<(AssistantTurnDto? Turn, string? Error)> SendAsync(Guid conversationId, string text, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"/conversations/{conversationId:D}/messages", new Body(text), cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => (await response.Content.ReadFromJsonAsync<AssistantTurnDto>(cancellationToken), null),
            HttpStatusCode.TooManyRequests => (null, "This conversation has reached its limit. Start a new one."),
            HttpStatusCode.BadRequest => (null, "The message is empty or too long."),
            HttpStatusCode.Conflict => (null, "The conversation is busy. Send one message at a time."),
            _ => (null, "The assistant is unavailable."),
        };
    }
}
