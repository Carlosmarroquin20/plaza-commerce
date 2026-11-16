using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
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
        CatalogDbContext db, CancellationToken cancellationToken, int page = 1, int pageSize = 20)
    {
        if (page is < 1 or > MaxPage || pageSize is < 1 or > MaxPageSize)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["paging"] = [$"page must be between 1 and {MaxPage} and pageSize between 1 and {MaxPageSize}."],
            });
        }

        var total = await db.Products.CountAsync(cancellationToken);
        var items = await db.Products.AsNoTracking()
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => ToResponse(p))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new ProductPage(items, page, pageSize, total));
    }

    private static async Task<Results<Ok<ProductResponse>, NotFound>> GetAsync(
        Guid id, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        return product is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(product));
    }

    /// <summary>Returns the products that exist among <paramref name="ids"/>; unknown ids are omitted.</summary>
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
            .Select(p => ToResponse(p))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(products);
    }

    /// <summary>Hybrid search: keywords and similarity, best matches first (ADR 0013).</summary>
    private static async Task<Results<Ok<List<ProductSearchHit>>, ValidationProblem>> SearchAsync(
        string? q, ProductSearch search, CancellationToken cancellationToken, int limit = 5, long? maxPriceCents = null, string? mode = null)
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

        return TypedResults.Ok(await search.SearchAsync(q, limit, maxPriceCents, searchMode, cancellationToken));
    }

    private static ProductResponse ToResponse(Product p) =>
        new(p.Id, p.Sku, p.Name, p.Description, p.Category, p.PriceCents, p.Currency);
}
