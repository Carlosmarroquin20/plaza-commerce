using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Plaza.Catalog.Data;

/// <summary>Loads the invented demo catalogue into an empty database.</summary>
public static class CatalogSeeder
{
    private sealed record SeedProduct(Guid Id, string Sku, string Name, string Description, string Category, long PriceCents);

    public static async Task SeedAsync(CatalogDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Products.AnyAsync(cancellationToken))
        {
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "seed", "products.json");
        await using var stream = File.OpenRead(path);
        var products = await JsonSerializer.DeserializeAsync<List<SeedProduct>>(
            stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken) ?? [];

        db.Products.AddRange(products.Select(p => new Product
        {
            Id = p.Id,
            Sku = p.Sku,
            Name = p.Name,
            Description = p.Description,
            Category = p.Category,
            PriceCents = p.PriceCents,
            Currency = "USD",
        }));
        await db.SaveChangesAsync(cancellationToken);
    }
}
