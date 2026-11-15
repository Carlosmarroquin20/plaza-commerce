using Microsoft.EntityFrameworkCore;
using Plaza.Messaging;

namespace Plaza.Notifications.Data;

/// <summary>
/// A notification that was sent. Refers to the customer by id only; contact details would be
/// looked up at send time and are not copied here.
/// </summary>
public sealed class Notification
{
    public required Guid Id { get; init; }

    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string Kind { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddMessagingTables();

        modelBuilder.Entity<Notification>(e =>
        {
            e.Property(n => n.Id).ValueGeneratedNever();
            e.Property(n => n.Kind).HasMaxLength(64);
            // A customer is told once about each thing that happens to an order.
            e.HasIndex(n => new { n.OrderId, n.Kind }).IsUnique();
        });
    }
}
