using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plaza.Messaging.Inbox;
using Plaza.Messaging.Outbox;

namespace Plaza.Messaging;

public static class MessagingDbContextExtensions
{
    /// <summary>Maps the outbox and inbox tables. Call from <c>OnModelCreating</c>.</summary>
    public static ModelBuilder AddMessagingTables(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Sequence);
            e.Property(x => x.Sequence).HasColumnName("sequence").UseIdentityAlwaysColumn();
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Topic).HasColumnName("topic").HasMaxLength(255);
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(255);
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(255);
            e.Property(x => x.Payload).HasColumnName("payload");
            e.Property(x => x.TraceParent).HasColumnName("trace_parent").HasMaxLength(64);
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.PublishedAt).HasColumnName("published_at");
            e.HasIndex(x => x.Id).IsUnique();
            // The publisher only ever looks at unpublished rows.
            e.HasIndex(x => x.Sequence)
                .HasDatabaseName("ix_outbox_messages_unpublished")
                .HasFilter("published_at IS NULL");
        });

        modelBuilder.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
            e.Property(x => x.Consumer).HasColumnName("consumer").HasMaxLength(255);
            e.Property(x => x.MessageId).HasColumnName("message_id");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        });

        return modelBuilder;
    }

    /// <summary>
    /// Adds a message to the outbox. It is published only if the surrounding
    /// <c>SaveChanges</c> commits. Returns the message id.
    /// </summary>
    public static Guid EnqueueMessage<TMessage>(this DbContext db, string topic, string key, TMessage message)
        where TMessage : IMessage
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(message);

        var outboxMessage = new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Topic = topic,
            Key = key,
            Type = TMessage.MessageType,
            Payload = JsonSerializer.Serialize(message, MessagingDefaults.Json),
            TraceParent = Activity.Current?.Id,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        db.Set<OutboxMessage>().Add(outboxMessage);
        return outboxMessage.Id;
    }
}
