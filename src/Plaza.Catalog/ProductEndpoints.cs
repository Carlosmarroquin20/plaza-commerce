using System.Linq.Expressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Plaza.Catalog.Data;
using Plaza.Catalog.Search;

namespace Plaza.Catalog;

public sealed record ProductResponse(
    Guid Id, string Sku, string Name, string Description, string Category, long PriceCents, string Currency);

public sealed record ProductPage(IReadOnlyList<ProductResponse> Items, int Page, int PageSize, int Total);

public static class ProductEndpoints
{
    private const int MaxPage = 100_000; // keeps (page - 1) * pageSize far from overflow
    private const int MaxPageSize = 100;
    private const int MaxBatchSize = 50;
    private const int MaxQueryLength = 200;
    private const int MaxSearchResults = 20;

    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/products");
        products.MapGet("/", ListAsync);
        products.MapGet("/{id:guid}", GetAsync);
        products.MapGet("/batch", GetBatchAsync);
        products.MapGet("/search", SearchAsync);
        return app;
    }

    private static async Task<Results<Ok<ProductPage>, ValidationProblem>> ListAsync(
        CatalogDbContext db, HybridCache cache, IConfiguration configuration, CancellationToken cancellationToken, int page = 1, int pageSize = 20)
    {
        if (page is < 1 or > MaxPage || pageSize is < 1 or > MaxPageSize)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["paging"] = [$"page must be between 1 and {MaxPage} and pageSize between 1 and {MaxPageSize}."],
            });
        }

        // Everybody sees the same catalogue page, so it is read from the database once per
        // cache period instead of once per visitor (ADR 0020).
        var result = await cache.GetOrCreateAsync(
            $"products:{page}:{pageSize}",
            async token =>
            {
                var total = await db.Products.CountAsync(token);
                var items = await db.Products.AsNoTracking()
                    .OrderBy(p => p.Name).ThenBy(p => p.Id)
                    .Skip((page - 1) * pageSize).Take(pageSize)
                    .Select(Projection)
                    .ToListAsync(token);
                return new ProductPage(items, page, pageSize, total);
            },
            CacheFor(configuration),
            cancellationToken: cancellationToken);

        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<ProductResponse>, NotFound>> GetAsync(
        Guid id, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().Where(p => p.Id == id).Select(Projection).FirstOrDefaultAsync(cancellationToken);
        return product is null ? TypedResults.NotFound() : TypedResults.Ok(product);
    }

    /// <summary>
    /// Returns the products that exist among <paramref name="ids"/>; unknown ids are omitted.
    /// Not cached: Orders prices orders from this answer.
    /// </summary>
    private static async Task<Results<Ok<List<ProductResponse>>, ValidationProblem>> GetBatchAsync(
        Guid[] ids, CatalogDbContext db, CancellationToken cancellationToken)
    {
        if (ids.Length is 0 or > MaxBatchSize)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["ids"] = [$"Between 1 and {MaxBatchSize} ids are required."],
            });
        }

        var products = await db.Products.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(Projection)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(products);
    }

    /// <summary>Hybrid search: keywords and similarity, best matches first (ADR 0013).</summary>
    private static async Task<Results<Ok<List<ProductSearchHit>>, ValidationProblem>> SearchAsync(
        string? q,
        ProductSearch search,
        HybridCache cache,
        IConfiguration configuration,
        CancellationToken cancellationToken,
        int limit = 5,
        long? maxPriceCents = null,
        string? mode = null)
    {
        var searchMode = SearchMode.Hybrid;
        var modeIsValid = mode is null || (Enum.TryParse(mode, ignoreCase: true, out searchMode) && Enum.IsDefined(searchMode));
        if (!modeIsValid || string.IsNullOrWhiteSpace(q) || q.Length > MaxQueryLength
            || limit is < 1 or > MaxSearchResults || maxPriceCents is < 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["search"] =
                [
                    $"q is required (at most {MaxQueryLength} characters), limit must be between 1 and {MaxSearchResults} " +
                    "maxPriceCents cannot be negative and mode is hybrid, keyword or vector.",
                ],
            });
        }

        // Popular searches repeat. The same question gets the same answer for the cache period.
        var hits = await cache.GetOrCreateAsync(
            $"search:{searchMode}:{limit}:{maxPriceCents}:{q.Trim().ToLowerInvariant()}",
            async token => await search.SearchAsync(q, limit, maxPriceCents, searchMode, token),
            CacheFor(configuration),
            cancellationToken: cancellationToken);

        return TypedResults.Ok(hits);
    }

    /// <summary>
    /// Translated to SQL, so only these columns are read. Mapping whole entities in memory
    /// would also fetch each product's embedding and search vector to throw them away.
    /// </summary>
    private static readonly Expression<Func<Product, ProductResponse>> Projection = p =>
        new ProductResponse(p.Id, p.Sku, p.Name, p.Description, p.Category, p.PriceCents, p.Currency);

    /// <summary>How long catalogue reads may be served from memory; <c>Catalog:CacheSeconds</c>, 0 turns it off.</summary>
    private static HybridCacheEntryOptions CacheFor(IConfiguration configuration)
    {
        var seconds = Math.Clamp(configuration.GetValue("Catalog:CacheSeconds", 30), 0, 3600);
        return seconds == 0
            ? new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableLocalCache | HybridCacheEntryFlags.DisableDistributedCache }
            : new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(seconds), LocalCacheExpiration = TimeSpan.FromSeconds(seconds) };
    }
}
