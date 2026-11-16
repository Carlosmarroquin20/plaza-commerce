using Anthropic;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Plaza.Assistant.Models;

/// <summary>
/// Builds the model the assistant talks to. Everything else in the service sees only
/// <see cref="IChatClient"/>, so changing provider is configuration, not code (ADR 0012).
/// </summary>
public static class ChatClientFactory
{
    public const string DefaultAnthropicModel = "claude-opus-5-5";

    public static IChatClient Create(AssistantOptions options, IServiceProvider services)
    {
        var provider = options.Provider.Trim().ToLowerInvariant();
        IChatClient model = provider switch
        {
            "scripted" => new ScriptedShoppingModel(),

            // The SDK reads its credentials from the environment (ANTHROPIC_API_KEY).
            "anthropic" => new AnthropicClient().AsIChatClient(
                string.IsNullOrWhiteSpace(options.Model) ? DefaultAnthropicModel : options.Model),

            "openai" => new OpenAI.Chat.ChatClient(
                RequireModel(options, provider),
                Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                    ?? throw new InvalidOperationException("OPENAI_API_KEY is not set.")).AsIChatClient(),

            "ollama" => new OllamaApiClient(new Uri(options.OllamaEndpoint), RequireModel(options, provider)),

            _ => throw new InvalidOperationException(
                $"Unknown Assistant:Provider '{options.Provider}'. Use scripted, anthropic, openai or ollama."),
        };

        // The tool loop is the same for every provider: the model asks for a tool, this runs
        // it and feeds the result back, at most MaxToolRounds times per turn.
        return new ChatClientBuilder(model)
            .UseFunctionInvocation(configure: invoker => invoker.MaximumIterationsPerRequest = options.MaxToolRounds)
            .Build(services);
    }

    private static string RequireModel(AssistantOptions options, string provider) =>
        string.IsNullOrWhiteSpace(options.Model)
            ? throw new InvalidOperationException($"Assistant:Model is required for the {provider} provider.")
            : options.Model;
}
