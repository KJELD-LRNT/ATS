using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Inventory;

public readonly record struct PoolBalanceSnapshot(int Available, int Outstanding)
{
    public int OnHand => Available + Outstanding;
}

/// <summary>
/// Atomic pool balance moves. Each method is a single conditional UPDATE (never read-modify-write), so
/// concurrent callers can never take a pool below zero. They execute immediately, outside SaveChanges:
/// callers that also write loans/stock events/audit must wrap everything in one explicit transaction
/// (<c>db.Database.BeginTransactionAsync()</c>). Tracked <see cref="Domain.Pool"/> entities are not refreshed.
/// </summary>
public static class PoolBalance
{
    /// <summary>Checkout: moves <paramref name="quantity"/> units from Available to Outstanding. False when not enough are available.</summary>
    public static async Task<bool> TryTakeAsync(AppDbContext db, int poolId, int quantity, CancellationToken ct = default)
    {
        EnsurePositive(quantity);
        var rows = await db.Pools.Where(p => p.Id == poolId && p.Available >= quantity)
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Available, p => p.Available - quantity)
                .SetProperty(p => p.Outstanding, p => p.Outstanding + quantity), ct);
        return rows == 1;
    }

    /// <summary>Return: moves units from Outstanding back to Available. Throws <see cref="ConflictException"/> if fewer are outstanding.</summary>
    public static async Task ReturnAsync(AppDbContext db, int poolId, int quantity, CancellationToken ct = default)
    {
        EnsurePositive(quantity);
        var rows = await db.Pools.Where(p => p.Id == poolId && p.Outstanding >= quantity)
            .ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Available, p => p.Available + quantity)
                .SetProperty(p => p.Outstanding, p => p.Outstanding - quantity), ct);
        if (rows != 1)
            throw new ConflictException("pool.outstanding_insufficient",
                $"Cannot return {quantity} unit(s): fewer are outstanding for this pool.");
    }

    /// <summary>Receipt / inbound transfer: adds units to Available (and therefore on-hand).</summary>
    public static async Task AddAvailableAsync(AppDbContext db, int poolId, int quantity, CancellationToken ct = default)
    {
        EnsurePositive(quantity);
        var rows = await db.Pools.Where(p => p.Id == poolId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Available, p => p.Available + quantity), ct);
        if (rows != 1) throw new AccessDeniedException();
    }

    /// <summary>Loss / outbound transfer: removes units from Available. False when not enough are available.</summary>
    public static async Task<bool> TryRemoveAvailableAsync(AppDbContext db, int poolId, int quantity, CancellationToken ct = default)
    {
        EnsurePositive(quantity);
        var rows = await db.Pools.Where(p => p.Id == poolId && p.Available >= quantity)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Available, p => p.Available - quantity), ct);
        return rows == 1;
    }

    /// <summary>Reads the current balance from the database (inside the caller's transaction, if any).</summary>
    public static async Task<PoolBalanceSnapshot> ReadAsync(AppDbContext db, int poolId, CancellationToken ct = default)
    {
        var b = await db.Pools.AsNoTracking().Where(p => p.Id == poolId)
            .Select(p => new { p.Available, p.Outstanding }).FirstOrDefaultAsync(ct)
            ?? throw new AccessDeniedException();
        return new PoolBalanceSnapshot(b.Available, b.Outstanding);
    }

    private static void EnsurePositive(int quantity)
    {
        if (quantity <= 0) throw new DomainException("pool.quantity_positive", "Quantity must be greater than zero.");
    }
}
