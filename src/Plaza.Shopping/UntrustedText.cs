using System.Text.RegularExpressions;

namespace Plaza.Shopping;

/// <summary>
/// Recognises text that tries to give the model orders. Pattern matching is a filter, not a
/// defence: it stops the common attempts cheaply and will miss inventive ones. What actually
/// bounds the damage of a successful injection is that the model has nothing dangerous to
/// call (ADR 0014).
/// </summary>
public static partial class InjectionPatterns
{
    public static bool LooksLikeInstructions(string text)
    {
        try
        {
            return Pattern().IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Text built to make the filter slow is not text to pass on.
            return true;
        }
    }

    [GeneratedRegex(
        """
        (ignore|disregard|forget|override)\s+(all\s+|any\s+|the\s+|your\s+|my\s+)*(previous|prior|above|earlier|preceding|system|original)\s+(instructions?|rules?|prompts?|messages?|guidelines?)
        | (reveal|show|print|repeat|output|display|leak|tell\s+me|give\s+me|what\s+(is|are))\s+.{0,40}(system\s+prompt|initial\s+prompt|your\s+(instructions|rules|prompt|guidelines)|hidden\s+instructions)
        | you\s+are\s+now\s+ | from\s+now\s+on\s+you | developer\s+mode | jailbreak | \bDAN\b
        | new\s+instructions?\s*: | ^\s*(system|assistant|developer)\s*: | </?\s*(system|assistant|instructions?|tool)\s*>
        | (ignora|olvida|omite)\s+.{0,20}instrucciones | (muestra|revela|dime)\s+.{0,30}(prompt|instrucciones)
        | set\s+(the\s+)?(price|total)\s+to
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Multiline | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex Pattern();

    /// <summary>
    /// Stricter check for text that is supposed to describe a product. A customer may well say
    /// "add 2 units"; a product description has no business saying it, or addressing the assistant.
    /// </summary>
    public static bool LooksLikeInstructionsInCatalogueText(string text)
    {
        if (LooksLikeInstructions(text))
        {
            return true;
        }

        try
        {
            return CataloguePattern().IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    [GeneratedRegex(
        """
        \b(assistant|ai|model|chatbot|agent|llm)\b\s*[,:]
        | add\s+\d+\s+(units?|items?|pieces?|of\b) | \b(you\s+must|you\s+should|always|never)\s+(add|buy|recommend|include|tell|say)
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex CataloguePattern();
}

/// <summary>Text that comes from outside (the catalogue) and ends up in front of the model.</summary>
public static class UntrustedText
{
    public const string Removed = "[removed: this text looked like instructions, not a product description]";

    /// <summary>Returns the text, or a placeholder when it reads like an instruction to the model.</summary>
    public static string Neutralize(string text, Action? onRemoved = null)
    {
        if (!InjectionPatterns.LooksLikeInstructionsInCatalogueText(text))
        {
            return text;
        }

        onRemoved?.Invoke();
        return Removed;
    }
}
