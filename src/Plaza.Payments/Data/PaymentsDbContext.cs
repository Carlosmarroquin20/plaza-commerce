using Microsoft.EntityFrameworkCore;
using Plaza.Messaging;

namespace Plaza.Payments.Data;

public enum PaymentStatus
{
    Succeeded,
    Failed,

    /// <summary>Charged, then given back because the order was cancelled.</summary>
    Refunded,
}

/// <summary>
/// The outcome of charging an order. Holds no card data and not even the payment token:
/// only the provider's reference for a successful charge.
/// </summary>
public sealed class Payment
{
    public required Guid Id { get; init; }

    public required Guid OrderId { get; init; }

    public required long AmountCents { get; init; }

    public required string Currency { get; init; }

    public required PaymentStatus Status { get; set; }

    public string? ProviderReference { get; init; }

    public string? FailureReason { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? RefundedAt { get; set; }
}

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddMessagingTables();

        modelBuilder.Entity<Payment>(e =>
        {
            e.Property(p => p.Id).ValueGeneratedNever();
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(p => p.ProviderReference).HasMaxLength(128);
            e.Property(p => p.FailureReason).HasMaxLength(128);
            // One payment attempt per order: the database refuses a second charge record
            // even if two different messages ask for it.
            e.HasIndex(p => p.OrderId).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_payments_amount_positive", "amount_cents > 0"));
        });
    }
}
