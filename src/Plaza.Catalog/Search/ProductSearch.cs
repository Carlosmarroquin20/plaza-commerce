using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Plaza.Catalog.Data;

namespace Plaza.Catalog.Search;

public sealed record ProductSearchHit(
    Guid Id, string Sku, string Name, string Description, string Category, long PriceCents, string Currency, double Score);

/// <summary>Which rankings take part. Anything but <see cref="Hybrid"/> exists to measure the other two.</summary>
public enum SearchMode
{
    Hybrid,
    Keyword,
    Vector,
}

/// <summary>
/// Hybrid product search (ADR 0013): PostgreSQL full-text search and pgvector similarity each
/// rank the catalogue, and the two rankings are merged with reciprocal rank fusion.
/// </summary>
public sealed class ProductSearch(CatalogDbContext db, IEmbeddingGenerator<string, Embedding<float>> embeddings)
{
    /// <summary>How many candidates each ranking contributes before fusion.</summary>
    private const string CandidatesPerRanking = "20";

    /// <summary>The constant of reciprocal rank fusion; 60 is the value from the original paper.</summary>
    private const string RrfK = "60";

    /// <summary>Vector candidates further than this cosine distance are noise, not matches.</summary>
    private const string MaxCosineDistance = "0.95";

    // Keywords are OR-ed: "desk for a small room" should find desks even though no product
    // mentions rooms. plainto_tsquery does the stemming and drops stop words; ranking by
    // ts_rank_cd then favours products that match more of the words.
    // Only constants are interpolated here; every value from the caller is a parameter.
    private const string Sql = $"""
        WITH query AS (
            SELECT to_tsquery('english', replace(plainto_tsquery('english', @q)::text, '&', '|')) AS terms
        ),
        keyword AS (
            SELECT p.id, ROW_NUMBER() OVER (ORDER BY ts_rank_cd(p.search_vector, query.terms) DESC, p.id) AS rank
            FROM products p, query
            WHERE @use_keyword AND p.search_vector @@ query.terms
              AND (@max_price IS NULL OR p.price_cents <= @max_price)
            LIMIT {CandidatesPerRanking}
        ),
        semantic AS (
            SELECT p.id, ROW_NUMBER() OVER (ORDER BY p.embedding <=> @embedding, p.id) AS rank
            FROM products p
            WHERE @use_vector AND p.embedding IS NOT NULL
              AND p.embedding <=> @embedding < {MaxCosineDistance}
              AND (@max_price IS NULL OR p.price_cents <= @max_price)
            ORDER BY p.embedding <=> @embedding, p.id
            LIMIT {CandidatesPerRanking}
        )
        SELECT p.id, p.sku, p.name, p.description, p.category, p.price_cents, p.currency,
               (COALESCE(1.0 / ({RrfK} + keyword.rank), 0) + COALESCE(1.0 / ({RrfK} + semantic.rank), 0))::float8 AS score
        FROM products p
        LEFT JOIN keyword ON keyword.id = p.id
        LEFT JOIN semantic ON semantic.id = p.id
        WHERE keyword.id IS NOT NULL OR semantic.id IS NOT NULL
        ORDER BY score DESC, p.id
        LIMIT @limit
        """;

    public async Task<List<ProductSearchHit>> SearchAsync(
        string query, int limit, long? maxPriceCents, SearchMode mode, CancellationToken cancellationToken)
    {
        var embedding = new Vector(await embeddings.GenerateVectorAsync(query, cancellationToken: cancellationToken));

        return await db.Database
            .SqlQueryRaw<ProductSearchHit>(
                Sql,
                new NpgsqlParameter("q", query),
                new NpgsqlParameter("embedding", embedding),
                // Typed explicitly: a null without a type cannot be compared with a bigint.
                new NpgsqlParameter("max_price", NpgsqlDbType.Bigint) { Value = (object?)maxPriceCents ?? DBNull.Value },
                new NpgsqlParameter("use_keyword", mode != SearchMode.Vector),
                new NpgsqlParameter("use_vector", mode != SearchMode.Keyword),
                new NpgsqlParameter("limit", limit))
            .ToListAsync(cancellationToken);
    }
}
/// <summary>Computes the embedding of every product that does not have one yet.</summary>
public static class ProductIndexer
{
    public static string TextOf(Product product) => $"{product.Name}. {product.Description} {product.Category}";

    public static async Task IndexMissingAsync(
        CatalogDbContext db, IEmbeddingGenerator<string, Embedding<float>> embeddings, CancellationToken cancellationToken = default)
    {
        var missing = await db.Products.Where(p => p.Embedding == null).ToListAsync(cancellationToken);
        if (missing.Count == 0)
        {
            return;
        }

        var vectors = await embeddings.GenerateAsync(missing.Select(TextOf), cancellationToken: cancellationToken);
        for (var i = 0; i < missing.Count; i++)
        {
            missing[i].Embedding = new Vector(vectors[i].Vector);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
