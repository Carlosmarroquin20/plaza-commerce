using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plaza.Shopping;

namespace Plaza.Assistant.Data;

/// <summary>One thing said in a conversation. Only what the customer and the assistant said is kept; tool traffic is not.</summary>
public sealed record StoredMessage(string Role, string Text);

public sealed class Conversation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public required Guid Id { get; init; }

    /// <summary>The conversation as a JSON array of <see cref="StoredMessage"/>.</summary>
    public string MessagesJson { get; private set; } = "[]";

    /// <summary>The cart most recently built in this conversation, as JSON.</summary>
    public string? CartJson { get; private set; }

    public int Turns { get; set; }

    /// <summary>Model cost so far in millionths of a dollar. Whole numbers, so sums are exact.</summary>
    public long SpentMicroUsd { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>PostgreSQL <c>xmin</c>; two requests on one conversation cannot both spend its budget.</summary>
    public uint Version { get; private set; }

    public List<StoredMessage> GetMessages() => JsonSerializer.Deserialize<List<StoredMessage>>(MessagesJson, Json) ?? [];

    public void SetMessages(IEnumerable<StoredMessage> messages) => MessagesJson = JsonSerializer.Serialize(messages, Json);

    public Cart? GetCart() => CartJson is null ? null : JsonSerializer.Deserialize<Cart>(CartJson, Json);

    public void SetCart(Cart cart) => CartJson = JsonSerializer.Serialize(cart, Json);
}

public sealed class AssistantDbContext(DbContextOptions<AssistantDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Conversation>(e =>
        {
            e.Property(c => c.Id).ValueGeneratedNever();
            e.Property(c => c.MessagesJson).HasColumnName("messages").HasColumnType("jsonb");
            e.Property(c => c.CartJson).HasColumnName("cart").HasColumnType("jsonb");
            e.Property(c => c.Version).IsRowVersion();
            e.ToTable(t => t.HasCheckConstraint("ck_conversations_spent_not_negative", "spent_micro_usd >= 0"));
        });
    }
}
