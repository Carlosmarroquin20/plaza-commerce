using Microsoft.Extensions.AI;

namespace Plaza.Catalog.Search;

/// <summary>
/// A deterministic embedding that needs no model: words and their character trigrams are
/// hashed into a fixed number of dimensions (the "hashing trick") and the vector is normalised.
/// Texts that share words, or parts of words, end up close together. It does not know that a
/// "screen" is a "monitor"; a learned model does, and replaces this class behind
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> without touching the search (ADR 0013).
/// </summary>
public sealed class HashingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>Must match the <c>vector(n)</c> column of the products table.</summary>
    public const int Dimensions = 256;

    private const float WordWeight = 1f;
    private const float TrigramWeight = 0.5f;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(Embed(v)))));

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    public static float[] Embed(string text)
    {
        var vector = new float[Dimensions];

        foreach (var word in Tokenize(text))
        {
            Add(vector, word, WordWeight);

            // Trigrams of the padded word make "keyboards", "keybord" and "keyboard" overlap.
            var padded = $"^{word}$";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                Add(vector, padded.Substring(i, 3), TrigramWeight);
            }
        }

        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }

    internal static IEnumerable<string> Tokenize(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    private static void Add(float[] vector, string feature, float weight)
    {
        // FNV-1a: string.GetHashCode is randomised per process and would give different
        // vectors on every start. One bit of the hash picks the sign, which keeps unrelated
        // features that land in the same dimension from always adding up.
        var hash = 2166136261u;
        foreach (var c in feature)
        {
            hash = (hash ^ c) * 16777619u;
        }

        var index = (int)(hash % Dimensions);
        vector[index] += (hash & 0x8000_0000) == 0 ? weight : -weight;
    }
}
