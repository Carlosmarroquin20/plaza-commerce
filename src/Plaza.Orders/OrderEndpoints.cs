using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Plaza.Contracts;
using Plaza.Messaging;
using Plaza.Orders.Data;
using Plaza.Orders.Domain;
using Plaza.ServiceDefaults;

namespace Plaza.Orders;

public sealed record PlaceOrderLine(Guid ProductId, int Quantity);

public sealed record PlaceOrderRequest(Guid CustomerId, string? PaymentToken, IReadOnlyList<PlaceOrderLine>? Lines);

public sealed record OrderItemResponse(Guid ProductId, string ProductName, long UnitPriceCents, int Quantity);

public sealed record OrderResponse(
    Guid Id,
    Guid CustomerId,
    string Status,
    long TotalCents,
    string Currency,
    string? FailureReason,
    IReadOnlyList<OrderItemResponse> Items,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt);

public static partial class OrderEndpoints
{
    public const int MaxLines = 50;
    public const int MaxQuantityPerProduct = 100;
    public const int MaxListed = 50;

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/orders");
        orders.MapPost("/", PlaceAsync);
        orders.MapGet("/{id:guid}", GetAsync).WithName("GetOrder");
        orders.MapGet("/", ListByCustomerAsync);
        return app;
    }

    /// <summary>
    /// Accepts an order and starts the checkout. The response is 202: the order is durable,
    /// but stock and payment are decided asynchronously; poll <c>GET /orders/{id}</c>.
    /// </summary>
    private static async Task<Results<AcceptedAtRoute<OrderResponse>, ValidationProblem, ProblemHttpResult>> PlaceAsync(
        PlaceOrderRequest request,
        OrdersDbContext db,
        CatalogClient catalog,
        IOptions<CheckoutOptions> checkout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        // Repeated products are merged, so one product is always one line downstream.
        var quantities = request.Lines!
            .GroupBy(l => l.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        if (quantities.Values.Any(q => q > MaxQuantityPerProduct))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["lines"] = [$"At most {MaxQuantityPerProduct} units per product."],
            });
        }

        IReadOnlyDictionary<Guid, CatalogProduct> products;
        try
        {
            products = await catalog.GetProductsAsync(quantities.Keys, cancellationToken);
        }
        catch (Exception ex) when (HttpResilience.IsUnavailable(ex, cancellationToken))
        {
            return TypedResults.Problem(
                title: "Catalog is unavailable", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var unknown = quantities.Keys.Where(id => !products.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["lines"] = [$"Unknown products: {string.Join(", ", unknown)}"],
            });
        }

        var currencies = products.Values.Select(p => p.Currency).Distinct().ToList();
        if (currencies.Count != 1)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["lines"] = ["All products in an order must be priced in the same currency."],
            });
        }

        var items = quantities
            .Select(q => new OrderItem
            {
                ProductId = q.Key,
                ProductName = products[q.Key].Name,
                UnitPriceCents = products[q.Key].PriceCents,
                Quantity = q.Value,
            })
            .ToList();

        var order = Order.Place(
            request.CustomerId,
            request.PaymentToken!,
            currencies[0],
            items,
            timeProvider.GetUtcNow(),
            checkout.Value.ReservationTtl);

        // The order and the event that starts the checkout commit together (ADR 0004).
        db.Orders.Add(order);
        db.EnqueueMessage(
            Topics.Orders,
            order.Id.ToString(),
            new OrderPlaced(order.Id, [.. items.Select(i => new OrderLine(i.ProductId, i.Quantity))]));
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.AcceptedAtRoute(ToResponse(order), "GetOrder", new { id = order.Id });
    }

    private static async Task<Results<Ok<OrderResponse>, NotFound>> GetAsync(
        Guid id, OrdersDbContext db, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        return order is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(order));
    }

    /// <summary>A customer's most recent orders, newest first. Internal: the caller vouches for the customer id.</summary>
    private static async Task<Results<Ok<List<OrderResponse>>, ValidationProblem>> ListByCustomerAsync(
        Guid customerId, OrdersDbContext db, CancellationToken cancellationToken, int limit = 20)
    {
        if (customerId == Guid.Empty || limit is < 1 or > MaxListed)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["query"] = [$"customerId is required and limit must be between 1 and {MaxListed}."],
            });
        }

        // Order ids are UUIDv7, so ordering by id is ordering by creation time.
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.CustomerId == customerId)
            .OrderByDescending(o => o.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(orders.ConvertAll(ToResponse));
    }

    internal static Dictionary<string, string[]> Validate(PlaceOrderRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.CustomerId == Guid.Empty)
        {
            errors["customerId"] = ["customerId is required."];
        }

        if (request.PaymentToken is null || !PaymentTokenPattern().IsMatch(request.PaymentToken))
        {
            // Rejecting anything that is not token-shaped also keeps card numbers out by construction.
            errors["paymentToken"] = ["paymentToken must be a payment provider token (tok_...)."];
        }

        if (request.Lines is null || request.Lines.Count is 0 or > MaxLines)
        {
            errors["lines"] = [$"Between 1 and {MaxLines} lines are required."];
        }
        else if (request.Lines.Any(l => l.ProductId == Guid.Empty || l.Quantity is < 1 or > MaxQuantityPerProduct))
        {
            errors["lines"] = [$"Each line needs a productId and a quantity between 1 and {MaxQuantityPerProduct}."];
        }

        return errors;
    }

    private static OrderResponse ToResponse(Order order) => new(
        order.Id,
        order.CustomerId,
        order.Status.ToString(),
        order.TotalCents,
        order.Currency,
        order.FailureReason,
        [.. order.Items.Select(i => new OrderItemResponse(i.ProductId, i.ProductName, i.UnitPriceCents, i.Quantity))],
        order.CreatedAt,
        order.UpdatedAt,
        order.ExpiresAt);

    [GeneratedRegex(@"\Atok_[A-Za-z0-9_]{4,64}\z")] // \z, not $: $ would accept a trailing newline
    private static partial Regex PaymentTokenPattern();
}
