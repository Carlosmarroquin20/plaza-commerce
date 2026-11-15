using Microsoft.EntityFrameworkCore;
using Plaza.Inventory.Data;

namespace Plaza.Inventory;

public sealed record ReservationLine(Guid ProductId, int Quantity);

public enum ReservationOutcome
{
    Reserved,
    InsufficientStock,
    InvalidRequest,
}

/// <summary>Reserves, commits and releases stock. Every method must run inside a database transaction.</summary>
public sealed class StockReservationService(InventoryDbContext db, TimeProvider timeProvider)
{
    private const string Savepoint = "reserve_order";

    /// <summary>
    /// Reserves every line of an order or none of them. Safe under concurrency: each line is a
    /// single conditional UPDATE, so two orders cannot both take the last unit.
    /// </summary>
    public async Task<ReservationOutcome> TryReserveAsync(
        Guid orderId, IReadOnlyCollection<ReservationLine> lines, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Stock must be reserved inside a transaction.");

        if (lines.Count == 0 || lines.Any(l => l.Quantity <= 0))
        {
            return ReservationOutcome.InvalidRequest;
        }

        // Merge repeated products, and always lock rows in the same order so that two orders
        // for the same products cannot deadlock each other.
        var merged = lines
            .GroupBy(l => l.ProductId)
            .Select(g => new ReservationLine(g.Key, g.Sum(l => l.Quantity)))
            .OrderBy(l => l.ProductId)
            .ToList();

        await transaction.CreateSavepointAsync(Savepoint, cancellationToken);

        foreach (var line in merged)
        {
            var updated = await db.StockItems
                .Where(s => s.ProductId == line.ProductId && s.Available >= line.Quantity)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(x => x.Available, x => x.Available - line.Quantity)
                        .SetProperty(x => x.Reserved, x => x.Reserved + line.Quantity),
                    cancellationToken);

            if (updated == 0)
            {
                // Unknown product or not enough units: undo the lines already taken.
                await transaction.RollbackToSavepointAsync(Savepoint, cancellationToken);
                return ReservationOutcome.InsufficientStock;
            }
        }

        var now = timeProvider.GetUtcNow();
        db.Reservations.AddRange(merged.Select(l => new Reservation
        {
            OrderId = orderId,
            ProductId = l.ProductId,
            Quantity = l.Quantity,
            Status = ReservationStatus.Reserved,
            CreatedAt = now,
        }));
        return ReservationOutcome.Reserved;
    }

    /// <summary>Turns the reservations of a confirmed order into a permanent stock decrease.</summary>
    public async Task CommitAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var reservations = await db.Reservations
            .Where(r => r.OrderId == orderId)
            .OrderBy(r => r.ProductId)
            .ToListAsync(cancellationToken);

        if (reservations.Count == 0)
        {
            throw new InvalidOperationException($"Order {orderId} has no reservations to commit.");
        }

        if (reservations.Any(r => r.Status == ReservationStatus.Released))
        {
            // The units may already belong to another order. Fail so the message is parked
            // on the dead-letter topic instead of selling stock that is not held.
            throw new InvalidOperationException($"Order {orderId} was cancelled; its reservations cannot be committed.");
        }

        foreach (var reservation in reservations.Where(r => r.Status == ReservationStatus.Reserved))
        {
            await db.StockItems
                .Where(s => s.ProductId == reservation.ProductId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.Reserved, x => x.Reserved - reservation.Quantity),
                    cancellationToken);
            reservation.Status = ReservationStatus.Committed;
        }
    }

    /// <summary>
    /// Gives the reserved units of a cancelled order back to available stock. An order that
    /// never got a reservation (rejected, or cancelled before it was processed) is a no-op.
    /// </summary>
    /// <returns>The number of reservations released by this call.</returns>
    public async Task<int> ReleaseAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var reservations = await db.Reservations
            .Where(r => r.OrderId == orderId)
            .OrderBy(r => r.ProductId)
            .ToListAsync(cancellationToken);

        if (reservations.Any(r => r.Status == ReservationStatus.Committed))
        {
            throw new InvalidOperationException($"Order {orderId} was confirmed; its reservations cannot be released.");
        }

        var released = 0;
        foreach (var reservation in reservations.Where(r => r.Status == ReservationStatus.Reserved))
        {
            await db.StockItems
                .Where(s => s.ProductId == reservation.ProductId)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(x => x.Available, x => x.Available + reservation.Quantity)
                        .SetProperty(x => x.Reserved, x => x.Reserved - reservation.Quantity),
                    cancellationToken);
            reservation.Status = ReservationStatus.Released;
            released++;
        }

        return released;
    }
}
