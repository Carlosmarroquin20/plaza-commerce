using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HotChocolate.Authorization;
using HotChocolate.CostAnalysis.Types;
using Plaza.Gateway.Clients;

namespace Plaza.Gateway.GraphQL;

public sealed class Query
{
    /// <summary>The catalogue, one page at a time, ordered by name.</summary>
    [ListSize(AssumedSize = 20, SlicingArguments = ["pageSize"], SizedFields = ["items"], RequireOneSlicingArgument = false)]
    public async Task<ProductPage> GetProductsAsync(
        CatalogClient catalog, CancellationToken cancellationToken, int page = 1, int pageSize = 20)
    {
        if (page is < 1 or > 10_000 || pageSize is < 1 or > Limits.MaxPageSize)
        {
            throw Errors.BadInput($"page must be between 1 and 10000 and pageSize between 1 and {Limits.MaxPageSize}.");
        }

        var result = await catalog.ListAsync(page, pageSize, cancellationToken);
        return new ProductPage([.. result.Items.Select(Product.From)], result.Page, result.PageSize, result.Total);
    }

    public async Task<Product?> GetProductAsync(Guid id, ProductByIdDataLoader products, CancellationToken cancellationToken) =>
        await products.LoadAsync(id, cancellationToken);

    /// <summary>Searches the catalogue by keywords and similarity, best matches first.</summary>
    [ListSize(AssumedSize = 5, SlicingArguments = ["first"], RequireOneSlicingArgument = false)]
    public async Task<IReadOnlyList<Product>> SearchProductsAsync(
        string query, CatalogClient catalog, CancellationToken cancellationToken, int first = 5, long? maxPriceCents = null)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200 || first is < 1 or > Limits.MaxSearchResults || maxPriceCents is < 0)
        {
            throw Errors.BadInput(
                $"query is required (at most 200 characters), first must be between 1 and {Limits.MaxSearchResults} and maxPriceCents cannot be negative.");
        }

        return [.. (await catalog.SearchAsync(query, first, maxPriceCents, cancellationToken)).Select(Product.From)];
    }

    /// <summary>One of the caller's orders. An order that belongs to someone else does not exist for the caller.</summary>
    [Authorize]
    public async Task<Order?> GetOrderAsync(Guid id, ClaimsPrincipal user, OrdersClient orders, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(id, cancellationToken);
        return order is not null && order.CustomerId == user.CustomerId() ? Order.From(order) : null;
    }

    /// <summary>The caller's most recent orders, newest first.</summary>
    [Authorize]
    [ListSize(AssumedSize = 10, SlicingArguments = ["first"], RequireOneSlicingArgument = false)]
    public async Task<IReadOnlyList<Order>> GetMyOrdersAsync(
        ClaimsPrincipal user, OrdersClient orders, CancellationToken cancellationToken, int first = 10)
    {
        if (first is < 1 or > Limits.MaxOrders)
        {
            throw Errors.BadInput($"first must be between 1 and {Limits.MaxOrders}.");
        }

        return [.. (await orders.ListByCustomerAsync(user.CustomerId(), first, cancellationToken)).Select(Order.From)];
    }
}

public sealed class Mutation
{
    /// <summary>
    /// Places an order for the caller. The answer is immediate and the order starts as Pending:
    /// stock and payment are decided afterwards, so read the order again to follow it.
    /// </summary>
    [Authorize]
    public async Task<PlaceOrderPayload> PlaceOrderAsync(
        PlaceOrderInput input, ClaimsPrincipal user, OrdersClient orders, CancellationToken cancellationToken)
    {
        if (input.Lines is null || input.Lines.Count is 0 or > Limits.MaxOrderLines)
        {
            return new PlaceOrderPayload(null, [$"Between 1 and {Limits.MaxOrderLines} lines are required."]);
        }

        // The customer is who the token says, never who the request says.
        var result = await orders.PlaceAsync(
            user.CustomerId(),
            input.PaymentToken,
            [.. input.Lines.Select(l => new PlaceOrderLineDto(l.ProductId, l.Quantity))],
            cancellationToken);

        return new PlaceOrderPayload(result.Order is null ? null : Order.From(result.Order), result.Errors);
    }

    /// <summary>
    /// Sends a message to the shopping assistant. Reusing a conversation id continues that
    /// conversation; a conversation belongs to the caller who started it.
    /// </summary>
    [Authorize]
    [Cost(50)]
    public async Task<AskAssistantPayload> AskAssistantAsync(
        Guid conversationId, string text, ClaimsPrincipal user, AssistantClient assistant, CancellationToken cancellationToken)
    {
        if (conversationId == Guid.Empty || string.IsNullOrWhiteSpace(text) || text.Length > Limits.MaxMessageLength)
        {
            return new AskAssistantPayload(
                null, null, false, $"A conversation id and a message of 1 to {Limits.MaxMessageLength} characters are required.");
        }

        var (turn, error) = await assistant.SendAsync(OwnedConversation(user.CustomerId(), conversationId), text, cancellationToken);
        if (turn is null)
        {
            return new AskAssistantPayload(null, null, false, error);
        }

        var cart = turn.Cart is null
            ? null
            : new Cart(
                [.. turn.Cart.Lines.Select(l => new CartLine(l.ProductId, l.Name, l.UnitPriceCents, l.Quantity, l.LineTotalCents))],
                turn.Cart.TotalCents,
                turn.Cart.Currency,
                turn.Cart.Problems);
        return new AskAssistantPayload(turn.Reply, cart, turn.Blocked is not null, null);
    }

    /// <summary>
    /// The assistant identifies a conversation by its id alone. Deriving that id from the
    /// caller as well as from the id the caller chose means that knowing, or guessing, someone
    /// else's conversation id leads to a different conversation.
    /// </summary>
    internal static Guid OwnedConversation(Guid customerId, Guid conversationId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{customerId:N}:{conversationId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public static class Errors
{
    public static GraphQLException BadInput(string message) =>
        new(ErrorBuilder.New().SetMessage(message).SetCode("BAD_USER_INPUT").Build());
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The customer a token was issued to: its <c>sub</c> claim, which must be a GUID.</summary>
    public static Guid CustomerId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var id) && id != Guid.Empty
            ? id
            : throw new GraphQLException(
                ErrorBuilder.New().SetMessage("The access token has no usable subject.").SetCode("AUTH_NOT_AUTHORIZED").Build());
}
