using Microsoft.EntityFrameworkCore;
using Plaza.Messaging;

namespace Plaza.Inventory.Data;

/// <summary>Stock of one product. <c>Available + Reserved</c> is what is physically on hand.</summary>
public sealed class StockItem
{
    public required Guid ProductId { get; init; }

    /// <summary>Units that can still be sold.</summary>
    public int Available { get; set; }

    /// <summary>Units held for orders that are not confirmed yet.</summary>
    public int Reserved { get; set; }
}

public enum ReservationStatus
{
    /// <summary>Held for an order awaiting payment.</summary>
    Reserved,

    /// <summary>The order was confirmed; the units left the warehouse.</summary>
    Committed,

    /// <summary>The order was cancelled; the units went back to available stock.</summary>
    Released,
}

public sealed class Reservation
{
    public required Guid OrderId { get; init; }

    public required Guid ProductId { get; init; }

    public required int Quantity { get; init; }

    public ReservationStatus Status { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<StockItem> StockItems => Set<StockItem>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddMessagingTables();

        modelBuilder.Entity<StockItem>(e =>
        {
            e.HasKey(s => s.ProductId);
            e.Property(s => s.ProductId).ValueGeneratedNever();
            // Last line of defence against overselling: the database refuses negative stock.
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_stock_items_available_not_negative", "available >= 0");
                t.HasCheckConstraint("ck_stock_items_reserved_not_negative", "reserved >= 0");
            });
        });

        modelBuilder.Entity<Reservation>(e =>
        {
            e.HasKey(r => new { r.OrderId, r.ProductId });
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.ToTable(t => t.HasCheckConstraint("ck_reservations_quantity_positive", "quantity > 0"));
        });
    }
}
