using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Plaza.Inventory.Data;

/// <summary>Gives the demo catalogue its initial stock in an empty database.</summary>
public static class InventorySeeder
{
    private sealed record SeedProduct(Guid Id, int InitialStock);

    public static async Task SeedAsync(InventoryDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.StockItems.AnyAsync(cancellationToken))
        {
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "seed", "products.json");
        await using var stream = File.OpenRead(path);
        var products = await JsonSerializer.DeserializeAsync<List<SeedProduct>>(
            stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken) ?? [];

        db.StockItems.AddRange(products.Select(p => new StockItem { ProductId = p.Id, Available = p.InitialStock }));
        await db.SaveChangesAsync(cancellationToken);
    }
}
