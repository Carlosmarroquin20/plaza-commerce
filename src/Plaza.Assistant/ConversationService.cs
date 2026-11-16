using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Plaza.Assistant.Data;
using Plaza.Shopping;

namespace Plaza.Assistant;

public sealed record ConversationUsage(
    long InputTokens, long OutputTokens, decimal TurnCostUsd, decimal SpentUsd, decimal LimitUsd, int Turns);

/// <param name="Blocked">Why the assistant did not answer normally, or null when it did.</param>
public sealed record TurnResult(string Reply, Cart? Cart, string? Blocked, ConversationUsage Usage);

public static class BlockReasons
{
    public const string PromptInjection = "prompt_injection_suspected";
    public const string SystemPromptLeak = "system_prompt_leak";
    public const string BudgetExhausted = "conversation_budget_exhausted";
}

/// <summary>Runs one turn of a conversation: guard the input, let the model work, guard the output, account for the cost.</summary>
public sealed partial class ConversationService(
    AssistantDbContext db,
    IChatClient model,
    ShoppingService shopping,
    SystemPrompt systemPrompt,
    IOptions<AssistantOptions> options,
    TimeProvider timeProvider,
    ILogger<ConversationService> logger)
{
    public const string InjectionReply =
        "I can't act on that message. I can help you find products, check stock and build a cart.";

    public const string LeakReply = "I can't share that. I can help you find products, check stock and build a cart.";

    public const string BudgetReply =
        "This conversation has reached its limit. Please start a new one to keep shopping.";

    private const string RemovedFromHistory = "[message withheld by the input filter]";
    private const int MaxReplyCharacters = 4000;

    private readonly AssistantOptions _options = options.Value;

    public async Task<TurnResult> SendAsync(Guid conversationId, string text, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var conversation = await db.Conversations.FindAsync([conversationId], cancellationToken);
        if (conversation is null)
        {
            conversation = new Conversation { Id = conversationId, CreatedAt = now, UpdatedAt = now };
            db.Conversations.Add(conversation);
        }

        var limitMicroUsd = (long)(_options.MaxCostPerConversationUsd * 1_000_000m);

        // The limit is checked before the model is called, so a conversation can overshoot by
        // at most one turn, and a turn is itself bounded (output cap times tool rounds).
        if (conversation.SpentMicroUsd >= limitMicroUsd || conversation.Turns >= _options.MaxTurnsPerConversation)
        {
            return new TurnResult(BudgetReply, conversation.GetCart(), BlockReasons.BudgetExhausted, Usage(conversation, 0, limitMicroUsd));
        }

        var history = conversation.GetMessages();
        conversation.Turns++;
        conversation.UpdatedAt = now;

        if (InjectionPatterns.LooksLikeInstructions(text))
        {
            // Not sent to the model, and not kept where a later turn would send it either.
            LogBlocked(logger, conversationId, BlockReasons.PromptInjection);
            history.Add(new StoredMessage("user", RemovedFromHistory));
            history.Add(new StoredMessage("assistant", InjectionReply));
            conversation.SetMessages(history);
            await db.SaveChangesAsync(cancellationToken);
            return new TurnResult(InjectionReply, conversation.GetCart(), BlockReasons.PromptInjection, Usage(conversation, 0, limitMicroUsd));
        }

        var tools = new TurnTools(shopping);
        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt.Text) };
        messages.AddRange(history.TakeLast(_options.MaxHistoryMessages)
            .Select(m => new ChatMessage(m.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, m.Text)));
        messages.Add(new ChatMessage(ChatRole.User, text));

        var response = await model.GetResponseAsync(
            messages,
            new ChatOptions { Tools = tools.AsTools(), MaxOutputTokens = _options.MaxOutputTokens },
            cancellationToken);

        var reply = response.Text.Trim();
        string? blocked = null;
        if (systemPrompt.IsLeakedBy(reply))
        {
            LogBlocked(logger, conversationId, BlockReasons.SystemPromptLeak);
            reply = LeakReply;
            blocked = BlockReasons.SystemPromptLeak;
        }
        else if (reply.Length > MaxReplyCharacters)
        {
            reply = reply[..MaxReplyCharacters];
        }

        if (tools.NeutralizedTexts > 0)
        {
            LogNeutralized(logger, conversationId, tools.NeutralizedTexts);
        }

        // A provider that reports no usage must not make the conversation free: estimate it.
        var inputTokens = response.Usage?.InputTokenCount ?? messages.Sum(m => m.Text.Length) / 4;
        var outputTokens = response.Usage?.OutputTokenCount ?? Math.Max(1, reply.Length / 4);
        // Dollars per million tokens is the same number as millionths of a dollar per token.
        var costMicroUsd = (long)Math.Ceiling(
            (inputTokens * _options.InputUsdPerMillionTokens) + (outputTokens * _options.OutputUsdPerMillionTokens));

        conversation.InputTokens += inputTokens;
        conversation.OutputTokens += outputTokens;
        conversation.SpentMicroUsd += costMicroUsd;
        if (tools.Cart is not null)
        {
            conversation.SetCart(tools.Cart);
        }

        history.Add(new StoredMessage("user", text));
        history.Add(new StoredMessage("assistant", reply));
        conversation.SetMessages(history);
        await db.SaveChangesAsync(cancellationToken);

        return new TurnResult(reply, conversation.GetCart(), blocked, Usage(conversation, costMicroUsd, limitMicroUsd));
    }

    private static ConversationUsage Usage(Conversation conversation, long turnCostMicroUsd, long limitMicroUsd) => new(
        conversation.InputTokens,
        conversation.OutputTokens,
        turnCostMicroUsd / 1_000_000m,
        conversation.SpentMicroUsd / 1_000_000m,
        limitMicroUsd / 1_000_000m,
        conversation.Turns);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation {ConversationId}: turn blocked ({Reason})")]
    private static partial void LogBlocked(ILogger logger, Guid conversationId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Conversation {ConversationId}: {Count} catalogue text(s) withheld from the model because they read like instructions")]
    private static partial void LogNeutralized(ILogger logger, Guid conversationId, int count);
}
