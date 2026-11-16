using Microsoft.EntityFrameworkCore;
using Plaza.Inventory.Data;
using Plaza.Testing;
using Xunit;

[assembly: AssemblyFixture(typeof(Plaza.Inventory.Tests.Infrastructure))]

namespace Plaza.Inventory.Tests;

public sealed class Infrastructure : IAsyncLifetime
{
    public InfrastructureFixture Containers { get; } = new();

    public async ValueTask InitializeAsync() => await Containers.StartAsync();

    public ValueTask DisposeAsync() => Containers.DisposeAsync();
}

public sealed class StockReservationTests(Infrastructure infrastructure) : IAsyncLifetime
{
    private static readonly Guid Desk = Guid.NewGuid();
    private static readonly Guid Chair = Guid.NewGuid();

    private readonly DbContextOptions<InventoryDbContext> _options = new DbContextOptionsBuilder<InventoryDbContext>()
        .UseNpgsql(infrastructure.Containers.ConnectionString(InfrastructureFixture.UniqueName("inventory")))
        .UseSnakeCaseNamingConvention()
        .Options;

    public async ValueTask InitializeAsync()
    {
        await using var db = new InventoryDbContext(_options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Concurrent_orders_for_the_last_units_never_oversell()
    {
        await SetStockAsync(Desk, available: 5);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ =>
            Task.Run(() => ReserveAsync(Guid.NewGuid(), new ReservationLine(Desk, 1)))));

        Assert.Equal(5, outcomes.Count(o => o == ReservationOutcome.Reserved));
        Assert.Equal(35, outcomes.Count(o => o == ReservationOutcome.InsufficientStock));
        var stock = await GetStockAsync(Desk);
        Assert.Equal((0, 5), (stock.Available, stock.Reserved));
        Assert.Equal(5, await CountReservationsAsync());
    }

    [Fact]
    public async Task Order_is_reserved_completely_or_not_at_all()
    {
        await SetStockAsync(Desk, available: 10);
        await SetStockAsync(Chair, available: 1);

        var outcome = await ReserveAsync(
            Guid.NewGuid(), new ReservationLine(Desk, 2), new ReservationLine(Chair, 2));

        Assert.Equal(ReservationOutcome.InsufficientStock, outcome);
        Assert.Equal(10, (await GetStockAsync(Desk)).Available); // the desk line was rolled back
        Assert.Equal(1, (await GetStockAsync(Chair)).Available);
        Assert.Equal(0, await CountReservationsAsync());
    }

    [Fact]
    public async Task Unknown_product_is_rejected_as_insufficient_stock()
    {
        var outcome = await ReserveAsync(Guid.NewGuid(), new ReservationLine(Guid.NewGuid(), 1));

        Assert.Equal(ReservationOutcome.InsufficientStock, outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Non_positive_quantity_is_rejected(int quantity)
    {
        await SetStockAsync(Desk, available: 10);

        var outcome = await ReserveAsync(Guid.NewGuid(), new ReservationLine(Desk, quantity));

        Assert.Equal(ReservationOutcome.InvalidRequest, outcome);
        Assert.Equal(10, (await GetStockAsync(Desk)).Available);
    }

    [Fact]
    public async Task Repeated_product_lines_are_merged()
    {
        await SetStockAsync(Desk, available: 3);

        var outcome = await ReserveAsync(
            Guid.NewGuid(), new ReservationLine(Desk, 2), new ReservationLine(Desk, 2));

        // 2 + 2 exceeds 3; checking each line on its own would have let the first one through.
        Assert.Equal(ReservationOutcome.InsufficientStock, outcome);
        Assert.Equal(3, (await GetStockAsync(Desk)).Available);
    }

    [Fact]
    public async Task Orders_listing_products_in_opposite_order_do_not_deadlock()
    {
        await SetStockAsync(Desk, available: 1000);
        await SetStockAsync(Chair, available: 1000);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => i % 2 == 0
            ? ReserveAsync(Guid.NewGuid(), new ReservationLine(Desk, 1), new ReservationLine(Chair, 1))
            : ReserveAsync(Guid.NewGuid(), new ReservationLine(Chair, 1), new ReservationLine(Desk, 1)))));

        Assert.All(outcomes, o => Assert.Equal(ReservationOutcome.Reserved, o));
        Assert.Equal(960, (await GetStockAsync(Desk)).Available);
        Assert.Equal(960, (await GetStockAsync(Chair)).Available);
    }

    [Fact]
    public async Task Committing_a_reservation_releases_the_hold_once()
    {
        await SetStockAsync(Desk, available: 10);
        var orderId = Guid.NewGuid();
        await ReserveAsync(orderId, new ReservationLine(Desk, 4));

        await CommitAsync(orderId);
        await CommitAsync(orderId); // a second confirmation must not release more

        var stock = await GetStockAsync(Desk);
        Assert.Equal((6, 0), (stock.Available, stock.Reserved));
    }

    [Fact]
    public async Task Committing_an_order_without_reservations_fails()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CommitAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Releasing_a_reservation_returns_the_units_once()
    {
        await SetStockAsync(Desk, available: 10);
        await SetStockAsync(Chair, available: 3);
        var orderId = Guid.NewGuid();
        await ReserveAsync(orderId, new ReservationLine(Desk, 4), new ReservationLine(Chair, 3));

        Assert.Equal(2, await ReleaseAsync(orderId));
        Assert.Equal(0, await ReleaseAsync(orderId)); // a second cancellation must not add stock

        var desk = await GetStockAsync(Desk);
        var chair = await GetStockAsync(Chair);
        Assert.Equal((10, 0), (desk.Available, desk.Reserved));
        Assert.Equal((3, 0), (chair.Available, chair.Reserved));
    }

    [Fact]
    public async Task Released_units_can_be_reserved_by_another_order()
    {
        await SetStockAsync(Desk, available: 1);
        var cancelled = Guid.NewGuid();
        await ReserveAsync(cancelled, new ReservationLine(Desk, 1));
        Assert.Equal(ReservationOutcome.InsufficientStock, await ReserveAsync(Guid.NewGuid(), new ReservationLine(Desk, 1)));

        await ReleaseAsync(cancelled);

        Assert.Equal(ReservationOutcome.Reserved, await ReserveAsync(Guid.NewGuid(), new ReservationLine(Desk, 1)));
    }

    [Fact]
    public async Task Releasing_an_order_that_never_reserved_does_nothing()
    {
        Assert.Equal(0, await ReleaseAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Confirmed_and_cancelled_are_mutually_exclusive()
    {
        await SetStockAsync(Desk, available: 10);
        var confirmed = Guid.NewGuid();
        var cancelled = Guid.NewGuid();
        await ReserveAsync(confirmed, new ReservationLine(Desk, 1));
        await ReserveAsync(cancelled, new ReservationLine(Desk, 1));
        await CommitAsync(confirmed);
        await ReleaseAsync(cancelled);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ReleaseAsync(confirmed));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CommitAsync(cancelled));

        var stock = await GetStockAsync(Desk);
        Assert.Equal((9, 0), (stock.Available, stock.Reserved));
    }

    private async Task<int> ReleaseAsync(Guid orderId)
    {
        await using var db = new InventoryDbContext(_options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var released = await new StockReservationService(db, TimeProvider.System).ReleaseAsync(orderId, CancellationToken.None);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return released;
    }

    private async Task<ReservationOutcome> ReserveAsync(Guid orderId, params ReservationLine[] lines)
    {
        await using var db = new InventoryDbContext(_options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var outcome = await new StockReservationService(db, TimeProvider.System)
            .TryReserveAsync(orderId, lines, CancellationToken.None);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return outcome;
    }

    private async Task CommitAsync(Guid orderId)
    {
        await using var db = new InventoryDbContext(_options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await new StockReservationService(db, TimeProvider.System).CommitAsync(orderId, CancellationToken.None);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task SetStockAsync(Guid productId, int available)
    {
        await using var db = new InventoryDbContext(_options);
        db.StockItems.Add(new StockItem { ProductId = productId, Available = available });
        await db.SaveChangesAsync();
    }

    private async Task<StockItem> GetStockAsync(Guid productId)
    {
        await using var db = new InventoryDbContext(_options);
        return await db.StockItems.AsNoTracking().SingleAsync(s => s.ProductId == productId);
    }

    private async Task<int> CountReservationsAsync()
    {
        await using var db = new InventoryDbContext(_options);
        return await db.Reservations.CountAsync();
    }
}
