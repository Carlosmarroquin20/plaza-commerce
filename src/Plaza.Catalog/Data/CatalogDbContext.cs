using Microsoft.EntityFrameworkCore;

namespace Plaza.Catalog.Data;

public sealed class Product
{
    public required Guid Id { get; init; }

    public required string Sku { get; init; }

    public required string Name { get; set; }

    public required string Description { get; set; }

    public required string Category { get; set; }

    /// <summary>Price in minor units. Money is never stored as floating point.</summary>
    public required long PriceCents { get; set; }

    public required string Currency { get; set; }
}

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(e =>
        {
            e.Property(p => p.Id).ValueGeneratedNever();
            e.Property(p => p.Sku).HasMaxLength(64);
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.Description).HasMaxLength(2000);
            e.Property(p => p.Category).HasMaxLength(64);
            e.Property(p => p.Currency).HasMaxLength(3).IsFixedLength();
            e.HasIndex(p => p.Sku).IsUnique();
            e.HasIndex(p => p.Category);
            e.ToTable(t => t.HasCheckConstraint("ck_products_price_not_negative", "price_cents >= 0"));
        });
    }
}
