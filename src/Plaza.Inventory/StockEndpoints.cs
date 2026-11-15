using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Plaza.Inventory.Data;

namespace Plaza.Inventory;

public sealed record StockResponse(Guid ProductId, int Available, int Reserved);

public sealed record SetStockRequest(int Available);

public static class StockEndpoints
{
    private const int MaxUnits = 1_000_000;

    public static IEndpointRouteBuilder MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        var stock = app.MapGroup("/stock");
        stock.MapGet("/{productId:guid}", GetAsync);
        // Internal: back-office stock adjustment. Not exposed to customers.
        stock.MapPut("/{productId:guid}", SetAsync);
        return app;
    }

    private static async Task<Results<Ok<StockResponse>, NotFound>> GetAsync(
        Guid productId, InventoryDbContext db, CancellationToken cancellationToken)
    {
        var item = await db.StockItems.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProductId == productId, cancellationToken);
        return item is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new StockResponse(item.ProductId, item.Available, item.Reserved));
    }

    private static async Task<Results<Ok<StockResponse>, ValidationProblem>> SetAsync(
        Guid productId, SetStockRequest request, InventoryDbContext db, CancellationToken cancellationToken)
    {
        if (request.Available is < 0 or > MaxUnits)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["available"] = [$"available must be between 0 and {MaxUnits}."],
            });
        }

        // Single statement, so it cannot interleave with a concurrent reservation.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO stock_items (product_id, available, reserved)
            VALUES ({productId}, {request.Available}, 0)
            ON CONFLICT (product_id) DO UPDATE SET available = EXCLUDED.available
            """, cancellationToken);

        var item = await db.StockItems.AsNoTracking().SingleAsync(s => s.ProductId == productId, cancellationToken);
        return TypedResults.Ok(new StockResponse(item.ProductId, item.Available, item.Reserved));
    }
}
