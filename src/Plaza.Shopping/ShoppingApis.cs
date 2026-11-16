using System.Globalization;
using System.Net;
using System.Net.Http.Json;

namespace Plaza.Shopping;

public sealed record CatalogItem(
    Guid Id, string Sku, string Name, string Description, string Category, long PriceCents, string Currency);

/// <summary>Internal REST client for the Catalog service.</summary>
public sealed class CatalogApi(HttpClient http)
{
    public async Task<IReadOnlyList<CatalogItem>> SearchAsync(
        string query, int limit, long? maxPriceCents, CancellationToken cancellationToken)
    {
        var url = $"/products/search?q={Uri.EscapeDataString(query)}&limit={limit.ToString(CultureInfo.InvariantCulture)}";
        if (maxPriceCents is { } max)
        {
            url += $"&maxPriceCents={max.ToString(CultureInfo.InvariantCulture)}";
        }

        return await http.GetFromJsonAsync<List<CatalogItem>>(url, cancellationToken) ?? [];
    }

    /// <summary>Returns the products that exist; unknown ids are absent from the result.</summary>
    public async Task<IReadOnlyDictionary<Guid, CatalogItem>> GetAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, CatalogItem>();
        }

        var url = "/products/batch?" + string.Join('&', ids.Select(id => $"ids={id:D}"));
        var products = await http.GetFromJsonAsync<List<CatalogItem>>(url, cancellationToken) ?? [];
        return products.ToDictionary(p => p.Id);
    }
}

/// <summary>Internal REST client for the Inventory service.</summary>
public sealed class InventoryApi(HttpClient http)
{
    private sealed record StockDto(Guid ProductId, int Available, int Reserved);

    /// <summary>Units that can be sold right now; 0 for a product Inventory does not know.</summary>
    public async Task<int> GetAvailableAsync(Guid productId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"/stock/{productId:D}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return 0;
        }

        response.EnsureSuccessStatusCode();
        var stock = await response.Content.ReadFromJsonAsync<StockDto>(cancellationToken);
        return stock?.Available ?? 0;
    }
}
