using Microsoft.EntityFrameworkCore;

namespace Plaza.PaymentProvider.Data;

public enum ChargeStatus
{
    /// <summary>Money was taken.</summary>
    Succeeded,

    /// <summary>The provider refused the charge; no money moved.</summary>
    Declined,

    /// <summary>Money was taken and then given back.</summary>
    Refunded,

    /// <summary>Cancelled before any money moved. A charge with this key is refused from now on.</summary>
    Voided,
}

/// <summary>
/// The provider's ledger: one row per idempotency key. Holds no card data and not the token,
/// only a hash of the request so that a key reused for a different request can be detected.
/// </summary>
public sealed class Charge
{
    /// <summary>ISO 4217 "no currency", used by a void that arrived before its charge.</summary>
    public const string NoCurrency = "XXX";

    public required Guid Id { get; init; }

    public required string IdempotencyKey { get; init; }

    public required string RequestHash { get; init; }

    /// <summary>The caller's own identifier for what is being paid (Plaza sends the order id).</summary>
    public required string Reference { get; init; }

    public required long AmountCents { get; init; }

    public required string Currency { get; init; }

    public required ChargeStatus Status { get; set; }

    public string? DeclineReason { get; init; }

    /// <summary>How many times a charge was requested with this key. Only the first one moved money.</summary>
    public int Attempts { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProviderDbContext(DbContextOptions<ProviderDbContext> options) : DbContext(options)
{
    public DbSet<Charge> Charges => Set<Charge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Charge>(e =>
        {
            e.Property(c => c.Id).ValueGeneratedNever();
            e.Property(c => c.IdempotencyKey).HasMaxLength(128);
            e.Property(c => c.RequestHash).HasMaxLength(64);
            e.Property(c => c.Reference).HasMaxLength(64);
            e.Property(c => c.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.DeclineReason).HasMaxLength(64);
            // The idempotency guarantee: the database admits one charge per key, whatever
            // the number of concurrent requests.
            e.HasIndex(c => c.IdempotencyKey).IsUnique();
            e.HasIndex(c => c.Reference);
            e.ToTable(t => t.HasCheckConstraint("ck_charges_amount_not_negative", "amount_cents >= 0"));
        });
    }
}
