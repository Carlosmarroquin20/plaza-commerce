namespace Plaza.Assistant;

public sealed class AssistantOptions
{
    /// <summary>
    /// Which model answers: <c>scripted</c> (deterministic, no network, the default),
    /// <c>anthropic</c>, <c>openai</c> or <c>ollama</c> (ADR 0012).
    /// </summary>
    public string Provider { get; set; } = "scripted";

    /// <summary>Model id for the provider. Empty means the provider's default.</summary>
    public string Model { get; set; } = "";

    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    /// <summary>What one conversation may cost. Once reached, the conversation is closed.</summary>
    public decimal MaxCostPerConversationUsd { get; set; } = 0.25m;

    public int MaxTurnsPerConversation { get; set; } = 30;

    public int MaxInputCharacters { get; set; } = 2000;

    /// <summary>
    /// Cap on what the model may produce per call, so one turn has a bounded cost. Models that
    /// think before answering spend part of it on thinking, so it is not set tight.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>How many times the model may go back to the tools within one turn.</summary>
    public int MaxToolRounds { get; set; } = 6;

    /// <summary>How much of the conversation is sent back to the model on each turn.</summary>
    public int MaxHistoryMessages { get; set; } = 20;

    /// <summary>
    /// Prices used to turn token counts into cost. The defaults are the list prices of the
    /// default Anthropic model; set them to match whatever model is configured.
    /// </summary>
    public decimal InputUsdPerMillionTokens { get; set; } = 4m;

    public decimal OutputUsdPerMillionTokens { get; set; } = 20m;

    /// <summary>Requests per minute accepted from one client address.</summary>
    public int RequestsPerMinute { get; set; } = 30;
}
