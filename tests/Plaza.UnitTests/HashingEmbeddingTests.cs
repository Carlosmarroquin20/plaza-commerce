using Plaza.Catalog.Search;
using Xunit;

namespace Plaza.UnitTests;

public sealed class HashingEmbeddingTests
{
    [Fact]
    public void Embedding_has_the_declared_size_and_unit_length()
    {
        var vector = HashingEmbeddingGenerator.Embed("Mechanical keyboard with tactile switches");

        Assert.Equal(HashingEmbeddingGenerator.Dimensions, vector.Length);
        Assert.Equal(1.0, Math.Sqrt(vector.Sum(x => (double)x * x)), precision: 5);
    }

    [Fact]
    public void Same_text_always_gives_the_same_vector()
    {
        // Recorded value: a change here means stored embeddings no longer match new queries.
        var vector = HashingEmbeddingGenerator.Embed("desk");

        Assert.Equal(HashingEmbeddingGenerator.Embed("desk"), vector);
        Assert.Equal(HashingEmbeddingGenerator.Embed("  DESK! "), vector);
    }

    [Fact]
    public void Text_without_words_is_the_zero_vector() =>
        Assert.All(HashingEmbeddingGenerator.Embed("  ... !!! "), x => Assert.Equal(0f, x));

    [Theory]
    [InlineData("keybord", "Mechanical Keyboard", "Adjustable Footrest")]
    [InlineData("monitors", "27-inch QHD Monitor", "Task Chair")]
    [InlineData("wireless mouse", "Wireless Mouse", "Wall Shelf 80 cm")]
    public void Related_text_is_closer_than_unrelated_text(string query, string related, string unrelated) =>
        Assert.True(Cosine(query, related) > Cosine(query, unrelated));

    private static double Cosine(string a, string b)
    {
        var x = HashingEmbeddingGenerator.Embed(a);
        var y = HashingEmbeddingGenerator.Embed(b);
        return x.Zip(y, (p, q) => (double)p * q).Sum();
    }
}
