using Microsoft.AspNetCore.WebUtilities;

namespace Plaza.Orders;

public sealed record CatalogProduct(Guid Id, string Name, long PriceCents, string Currency);

/// <summary>Internal REST client for the Catalog service. Prices come from here, never from the caller.</summary>
public sealed class CatalogClient(HttpClient http)
{
    /// <summary>Returns the products that exist; unknown ids are absent from the result.</summary>
    public async Task<IReadOnlyDictionary<Guid, CatalogProduct>> GetProductsAsync(
        IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var url = QueryHelpers.AddQueryString(
            "/products/batch", ids.Select(id => KeyValuePair.Create("ids", (string?)id.ToString())));

        var products = await http.GetFromJsonAsync<List<CatalogProduct>>(url, cancellationToken) ?? [];
        return products.ToDictionary(p => p.Id);
    }
}
