using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using Pgvector;
using Plaza.Catalog.Search;

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

    /// <summary>Embedding of name, description and category; null until the product is indexed.</summary>
    public Vector? Embedding { get; set; }

    /// <summary>Weighted full-text index of name, category and description, maintained by PostgreSQL.</summary>
    public NpgsqlTsVector SearchVector { get; private set; } = null!;
}

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

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

            // Hybrid search (ADR 0013): a generated tsvector with a GIN index for keywords and
            // an HNSW index over the embedding for similarity.
            // A word in the name counts more than one in the category, and that more than one
            // in the description, so "desk" ranks desks above a lamp that mentions a desk.
            e.Property(p => p.SearchVector).HasComputedColumnSql(
                """
                setweight(to_tsvector('english', name), 'A') ||
                setweight(to_tsvector('english', category), 'B') ||
                setweight(to_tsvector('english', description), 'C')
                """,
                stored: true);
            e.HasIndex(p => p.SearchVector).HasMethod("gin");
            e.Property(p => p.Embedding).HasColumnType($"vector({HashingEmbeddingGenerator.Dimensions})");
            e.HasIndex(p => p.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
            e.ToTable(t => t.HasCheckConstraint("ck_products_price_not_negative", "price_cents >= 0"));
        });
    }
}
