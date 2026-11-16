using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Plaza.Assistant.Data;
using Plaza.Shopping;

namespace Plaza.Assistant;

public sealed record SendMessageRequest(string? Text);

public sealed record ConversationResponse(
    Guid Id, IReadOnlyList<StoredMessage> Messages, Cart? Cart, int Turns, decimal SpentUsd, decimal LimitUsd);

public static class AssistantEndpoints
{
    public const string RateLimitPolicy = "per-client";

    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        var conversations = app.MapGroup("/conversations");
        conversations.MapPost("/{id:guid}/messages", SendAsync).RequireRateLimiting(RateLimitPolicy);
        conversations.MapGet("/{id:guid}", GetAsync);
        return app;
    }

    /// <summary>
    /// Sends the customer's message to the assistant. The conversation id is chosen by the
    /// client (any new GUID starts a conversation) and is the only thing that identifies it.
    /// </summary>
    private static async Task<Results<Ok<TurnResult>, ValidationProblem, ProblemHttpResult>> SendAsync(
        Guid id,
        SendMessageRequest request,
        ConversationService conversations,
        IOptions<AssistantOptions> options,
        CancellationToken cancellationToken)
    {
        var max = options.Value.MaxInputCharacters;
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > max)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["text"] = [$"A conversation id and a message of 1 to {max} characters are required."],
            });
        }

        try
        {
            var result = await conversations.SendAsync(id, request.Text, cancellationToken);
            return result.Blocked == BlockReasons.BudgetExhausted
                ? TypedResults.Problem(
                    title: "Conversation limit reached", detail: result.Reply, statusCode: StatusCodes.Status429TooManyRequests)
                : TypedResults.Ok(result);
        }
        catch (DbUpdateException)
        {
            // Two requests for the same conversation at once: one of them has to try again,
            // otherwise both would spend from the same budget.
            return TypedResults.Problem(
                title: "The conversation is busy", detail: "Send one message at a time.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return TypedResults.Problem(
                title: "The assistant is unavailable", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<Results<Ok<ConversationResponse>, NotFound>> GetAsync(
        Guid id, AssistantDbContext db, IOptions<AssistantOptions> options, CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        return conversation is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new ConversationResponse(
                conversation.Id,
                conversation.GetMessages(),
                conversation.GetCart(),
                conversation.Turns,
                conversation.SpentMicroUsd / 1_000_000m,
                options.Value.MaxCostPerConversationUsd));
    }
}
