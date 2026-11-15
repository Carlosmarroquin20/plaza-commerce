using Microsoft.EntityFrameworkCore;
using Plaza.Messaging;
using Plaza.Orders.Domain;

namespace Plaza.Orders.Data;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddMessagingTables();

        modelBuilder.Entity<Order>(e =>
        {
            e.Property(o => o.Id).ValueGeneratedNever();
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(o => o.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(o => o.PaymentToken).HasMaxLength(128);
            e.Property(o => o.FailureReason).HasMaxLength(Order.MaxFailureReasonLength);
            e.Property(o => o.Version).IsRowVersion();
            e.HasIndex(o => o.CustomerId);
            e.ToTable(t => t.HasCheckConstraint("ck_orders_total_not_negative", "total_cents >= 0"));

            e.OwnsMany(o => o.Items, items =>
            {
                items.ToTable("order_items");
                items.WithOwner().HasForeignKey("OrderId");
                items.HasKey("OrderId", nameof(OrderItem.ProductId));
                items.Property(i => i.ProductName).HasMaxLength(200);
                items.ToTable(t => t.HasCheckConstraint("ck_order_items_quantity_positive", "quantity > 0"));
            });
            e.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }
}
