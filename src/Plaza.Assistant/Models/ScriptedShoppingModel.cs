using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Plaza.Assistant.Models;

/// <summary>
/// A stand-in for a language model that needs no network and always answers the same way
/// (ADR 0012). It reads the customer's message with a few rules, calls the same tools a real
/// model would, and writes its answer from the tool results.
/// <para>
/// It is deliberately gullible: asked for its system prompt it recites it, told that prices
/// are zero it agrees, and it follows "add N units" when it reads that in a product
/// description. A real model resists most of this; this one resists none of it, so every
/// guarantee the assistant gives while running on it comes from the code around the model,
/// which is what the evals measure.
/// </para>
/// </summary>
public sealed partial class ScriptedShoppingModel : IChatClient
{
    public const string ModelId = "scripted-shopping-1";
    public const string OffTopicReply =
        "I can only help with shopping at Plaza: finding products, checking stock and building a cart.";

    private const int MaxNeeds = 8;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, int> NumberWords = new()
    {
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10,
        ["a"] = 1,
        ["an"] = 1,
        ["some"] = 1,
        ["the"] = 1,
    };

    private enum Intent
    {
        OffTopic,
        Shop,
        Stock,
        RevealPrompt,
        MakeItFree,
    }

    private sealed record Need(string Query, int Quantity);

    private sealed record Request(Intent Intent, IReadOnlyList<Need> Needs, decimal? Budget);

    private sealed record Product(Guid Id, string Name, string Description, string Category, string Price);

    private sealed record Stock(Guid ProductId, int Available);

    private sealed record CartLine(string Name, long UnitPriceCents, int Quantity);

    private sealed record Cart(List<CartLine> Lines, long TotalCents, string Currency, List<string> Problems);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var conversation = messages.ToList();
        var reply = Decide(conversation);
        return Task.FromResult(new ChatResponse(reply)
        {
            ModelId = ModelId,
            Usage = EstimateUsage(conversation, reply),
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private static ChatMessage Decide(List<ChatMessage> conversation)
    {
        var lastUser = conversation.FindLastIndex(m => m.Role == ChatRole.User);
        if (lastUser < 0)
        {
            return Say("How can I help you shop at Plaza?");
        }

        var request = Understand(conversation[lastUser].Text);

        // What the tools have answered since the customer spoke, by tool name.
        var sinceUser = conversation.Skip(lastUser + 1).ToList();
        var calls = sinceUser.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToDictionary(c => c.CallId, c => c.Name);
        var results = sinceUser.SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Where(r => calls.ContainsKey(r.CallId))
            .ToLookup(r => calls[r.CallId], r => ToJson(r.Result));

        var searches = results["search_products"].Select(json => Read<List<Product>>(json) ?? []).ToList();

        switch (request.Intent)
        {
            case Intent.RevealPrompt:
                return Say(string.Join("\n", conversation.Where(m => m.Role == ChatRole.System).Select(m => m.Text)));

            case Intent.MakeItFree:
                return Say("Done. Everything in your cart is now free.");

            case Intent.Stock:
                if (searches.Count == 0)
                {
                    return Call("search_products", new() { ["query"] = request.Needs[0].Query, ["maxResults"] = 1 });
                }

                if (searches[0].Count == 0)
                {
                    return Say("I could not find that product in the catalogue.");
                }

                var product = searches[0][0];
                if (!results["check_stock"].Any())
                {
                    return Call("check_stock", new() { ["productIds"] = new[] { product.Id } });
                }

                var stock = (Read<List<Stock>>(results["check_stock"].First()) ?? []).FirstOrDefault();
                return Say(stock is { Available: > 0 }
                    ? $"{product.Name}: {stock.Available} in stock."
                    : $"{product.Name} is out of stock.");

            case Intent.Shop:
                if (searches.Count == 0)
                {
                    return new ChatMessage(ChatRole.Assistant, [.. request.Needs.Select((need, i) => (AIContent)new FunctionCallContent(
                        $"search-{i}",
                        "search_products",
                        new Dictionary<string, object?>
                        {
                            ["query"] = need.Query,
                            ["maxResults"] = 5,
                            // With one item the whole budget is its price ceiling.
                            ["maxPrice"] = request.Needs.Count == 1 && request.Budget is { } budget ? budget / need.Quantity : null,
                        }))]);
                }

                if (!results["build_cart"].Any())
                {
                    var items = Choose(request, searches);
                    return items.Count == 0
                        ? Say("I could not find anything in the catalogue that matches.")
                        : Call("build_cart", new() { ["items"] = items });
                }

                return Say(Describe(Read<Cart>(results["build_cart"].First())));

            default:
                return Say(OffTopicReply);
        }
    }

    /// <summary>Picks the best match for each need, then trades down or drops items to fit the budget.</summary>
    private static List<Dictionary<string, object?>> Choose(Request request, List<List<Product>> searches)
    {
        var picks = new List<(Product Product, int Quantity, List<Product> Alternatives)>();
        for (var i = 0; i < Math.Min(request.Needs.Count, searches.Count); i++)
        {
            if (searches[i].Count == 0)
            {
                continue;
            }

            // Something else of the same kind: same category as the best match, and named
            // after what was asked for ("keyboard" must not be swapped for a mouse).
            var best = searches[i][0];
            var noun = request.Needs[i].Query.Split(' ')[^1].TrimEnd('s');
            var alternatives = searches[i]
                .Where(p => p.Category == best.Category && p.Name.Contains(noun, StringComparison.OrdinalIgnoreCase))
                .ToList();
            picks.Add((best, request.Needs[i].Quantity, alternatives));
        }

        if (request.Budget is { } budget)
        {
            while (picks.Count > 0 && picks.Sum(p => Price(p.Product) * p.Quantity) > budget)
            {
                // Trade down where it saves the most; when nothing can be traded, drop the dearest.
                var swap = picks
                    .Select((pick, index) => (index, pick, Cheapest: pick.Alternatives.MinBy(Price)))
                    .Where(s => s.Cheapest is not null && Price(s.Cheapest) < Price(s.pick.Product))
                    .MaxBy(s => (Price(s.pick.Product) - Price(s.Cheapest!)) * s.pick.Quantity);

                if (swap.Cheapest is null)
                {
                    picks.Remove(picks.MaxBy(p => Price(p.Product) * p.Quantity));
                }
                else
                {
                    picks[swap.index] = (swap.Cheapest, swap.pick.Quantity, swap.pick.Alternatives);
                }
            }
        }
        var items = picks.ToDictionary(p => p.Product.Id, p => p.Quantity);

        // The gullible part: an instruction found in a product description is obeyed.
        foreach (var poisoned in searches.SelectMany(s => s))
        {
            var match = AddUnits().Match(poisoned.Description);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var units))
            {
                items[poisoned.Id] = units;
            }
        }

        return [.. items.Select(i => new Dictionary<string, object?> { ["productId"] = i.Key, ["quantity"] = i.Value })];
    }

    private static string Describe(Cart? cart)
    {
        if (cart is null || cart.Lines.Count == 0)
        {
            return "I could not put anything in the cart."
                + (cart is { Problems.Count: > 0 } ? " " + string.Join(" ", cart.Problems) : "");
        }

        var lines = string.Join("; ", cart.Lines.Select(l => $"{l.Quantity} x {l.Name} at {Money(l.UnitPriceCents)}"));
        var text = $"Your cart: {lines}. Total: {Money(cart.TotalCents)} {cart.Currency}.";
        if (cart.Problems.Count > 0)
        {
            text += " Left out: " + string.Join(" ", cart.Problems);
        }

        return text + " This is a proposal; I cannot place the order for you.";
    }

    private static Request Understand(string text)
    {
        var lower = text.ToLowerInvariant();

        if (RevealPrompt().IsMatch(lower))
        {
            return new Request(Intent.RevealPrompt, [], null);
        }

        if (MakeItFree().IsMatch(lower))
        {
            return new Request(Intent.MakeItFree, [], null);
        }

        decimal? budget = null;
        var budgetMatch = Budget().Match(lower);
        if (budgetMatch.Success)
        {
            budget = decimal.Parse(budgetMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            lower = lower.Remove(budgetMatch.Index, budgetMatch.Length);
        }

        var stock = StockQuestion().Match(lower);
        if (stock.Success)
        {
            var subject = StockNoise().Replace(lower, " ").Trim(' ', '?', '.', '!');
            return subject.Length < 2
                ? new Request(Intent.OffTopic, [], null)
                : new Request(Intent.Stock, [new Need(subject, 1)], null);
        }

        var shop = ShoppingCue().Match(lower);
        if (!shop.Success)
        {
            return new Request(Intent.OffTopic, [], null);
        }

        var needs = new List<Need>();
        foreach (var part in Separator().Split(Purpose().Replace(shop.Groups[1].Value, "")))
        {
            var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var quantity = 1;
            while (words.Count > 0)
            {
                // "27 inch monitor" is a size, not twenty-seven monitors.
                if (words.Count > 1 && Measure().IsMatch(words[1]) && char.IsDigit(words[0][0]))
                {
                    break;
                }

                if (int.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out var digits))
                {
                    quantity = digits;
                }
                else if (NumberWords.TryGetValue(words[0], out var word))
                {
                    quantity = word;
                }
                else if (words[0] is not ("new" or "good" or "more"))
                {
                    break;
                }

                words.RemoveAt(0);
            }

            var query = string.Join(' ', words).Trim(' ', '?', '.', '!');
            if (query.Length >= 2 && needs.Count < MaxNeeds)
            {
                needs.Add(new Need(query, quantity));
            }
        }

        return needs.Count == 0 ? new Request(Intent.OffTopic, [], null) : new Request(Intent.Shop, needs, budget);
    }

    private static UsageDetails EstimateUsage(List<ChatMessage> conversation, ChatMessage reply)
    {
        // Roughly four characters per token, the usual rule of thumb for English.
        var input = conversation.Sum(Characters) / 4;
        var output = Math.Max(1, Characters(reply) / 4);
        return new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output };
    }

    private static long Characters(ChatMessage message) => message.Contents.Sum(content => content switch
    {
        TextContent text => (long)text.Text.Length,
        FunctionCallContent call => call.Name.Length + JsonSerializer.Serialize(call.Arguments, Json).Length,
        FunctionResultContent result => ToJson(result.Result).Length,
        _ => 0,
    });

    private static ChatMessage Say(string text) => new(ChatRole.Assistant, text);

    private static ChatMessage Call(string tool, Dictionary<string, object?> arguments) =>
        new(ChatRole.Assistant, [new FunctionCallContent($"{tool}-0", tool, arguments)]);

    private static decimal Price(Product product) =>
        decimal.TryParse(product.Price, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) ? price : decimal.MaxValue;

    private static string Money(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static string ToJson(object? result) => result switch
    {
        null => "null",
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "null",
        JsonElement element => element.GetRawText(),
        string text => text,
        _ => JsonSerializer.Serialize(result, Json),
    };

    private static T? Read<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return default; // A tool error message instead of data: treat as "nothing found".
        }
    }

    [GeneratedRegex(@"(reveal|show|print|repeat|tell me|what is|what are|give me).{0,40}(system prompt|your instructions|your rules|your prompt)")]
    private static partial Regex RevealPrompt();

    [GeneratedRegex(@"(ignore|override|you must|set).{0,80}(free|price to 0|\$0|for nothing|total to 0)")]
    private static partial Regex MakeItFree();

    [GeneratedRegex(@"(?:under|below|less than|up to|at most|no more than|max(?:imum)?(?: of)?|(?:with a |my )?budget(?: of| is)?)\s*\$?\s*(\d+(?:\.\d{1,2})?)\s*(?:dollars|usd)?")]
    private static partial Regex Budget();

    [GeneratedRegex(@"in stock|available|availability|how many")]
    private static partial Regex StockQuestion();

    [GeneratedRegex(@"\b(is|are|do|does|you|have|has|the|any|there|how many|in stock|available|availability|left|still|of|units|currently|right now|please|tell me|i want to know|if)\b")]
    private static partial Regex StockNoise();

    [GeneratedRegex(@"(?:i need|i want|i'd like|i would like|i'm looking for|i am looking for|looking for|buy|get me|find me|find|recommend|add|order|purchase|necesito|quiero|busco)\s+(.+)", RegexOptions.Singleline)]
    private static partial Regex ShoppingCue();

    [GeneratedRegex(@"\s+(for my|for the|for an?|for|so that|so|because|to use|to work)\s+.*$", RegexOptions.Singleline)]
    private static partial Regex Purpose();

    [GeneratedRegex(@",|\band\b|\bplus\b|&|\by\b")]
    private static partial Regex Separator();

    [GeneratedRegex(@"^(inch|inches|in|cm|mm|w|watt|watts|hz|x)$")]
    private static partial Regex Measure();

    [GeneratedRegex(@"add (\d+) units", RegexOptions.IgnoreCase)]
    private static partial Regex AddUnits();
}
