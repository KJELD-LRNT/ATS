using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Inventory;

public sealed record CreatePoolInput(
    int TypeId, string Sku, int SiteId, string Description, int InitialQuantity = 0, string? Reason = null,
    int? MinimumStock = null);

/// <param name="BelowMinimum">Only pools that have a minimum stock level and are under it.</param>
public sealed record PoolSearch(
    string? Query = null, int? SiteId = null, int? TypeId = null,
    int Page = 1, int PageSize = Paging.DefaultPageSize, bool BelowMinimum = false);

/// <summary>
/// Site quantity pools and their stock ledger. Every balance change is a conditional atomic update plus a
/// <see cref="StockEvent"/> (with the resulting balances) and an <see cref="AuditEvent"/>, in one transaction.
/// Adjustments require a reason. Outstanding units only move through checkout/return (<see cref="PoolBalance"/>).
/// </summary>
public class PoolService(AppDbContext db, AccessScope access, AuditWriter audit, ICurrentUser user, IClock clock)
{
    public const int SkuMaxLength = 100;
    public const int ReasonMaxLength = 2000;

    public async Task<PagedResult<Pool>> SearchAsync(PoolSearch s, CancellationToken ct = default)
    {
        var q = (await access.VisiblePoolsAsync(ct)).AsNoTracking().Include(p => p.Site).Include(p => p.Type).AsQueryable();
        if (s.SiteId is { } siteId) q = q.Where(p => p.SiteId == siteId);
        if (s.TypeId is { } typeId) q = q.Where(p => p.TypeId == typeId);
        if (s.BelowMinimum) q = q.Where(p => p.MinimumStock != null && p.Available < p.MinimumStock);

        var term = s.Query?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var pattern = Paging.ContainsPattern(term);
            q = q.Where(p => EF.Functions.Like(p.Sku, pattern, @"\") || EF.Functions.Like(p.Description, pattern, @"\"));
        }

        return await q.OrderBy(p => p.Sku).ThenBy(p => p.Site!.Key).ToPageAsync(s.Page, s.PageSize, ct);
    }

    /// <summary>Returns the pool if visible to the current principal; otherwise <see cref="AccessDeniedException"/> (404).</summary>
    public async Task<Pool> GetAsync(int id, CancellationToken ct = default)
    {
        var pool = await (await access.VisiblePoolsAsync(ct)).AsNoTracking().Include(p => p.Site).Include(p => p.Type)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        return pool ?? throw new AccessDeniedException();
    }

    /// <summary>Stock ledger of a visible pool, newest first.</summary>
    public async Task<PagedResult<StockEvent>> GetStockEventsAsync(int poolId, int page = 1,
        int pageSize = Paging.DefaultPageSize, CancellationToken ct = default)
    {
        await GetAsync(poolId, ct);
        return await db.StockEvents.AsNoTracking().Where(e => e.PoolId == poolId)
            .OrderByDescending(e => e.Id).ToPageAsync(page, pageSize, ct);
    }

    /// <summary>Pools at other sites that can receive a transfer from this pool (same SKU and type).</summary>
    public async Task<IReadOnlyList<Pool>> GetTransferTargetsAsync(int poolId, CancellationToken ct = default)
    {
        access.EnsureIt();
        var source = await GetAsync(poolId, ct);
        return await db.Pools.AsNoTracking().Include(p => p.Site)
            .Where(p => p.Sku == source.Sku && p.TypeId == source.TypeId && p.Id != source.Id)
            .OrderBy(p => p.Site!.Key).ToListAsync(ct);
    }

    public async Task<Pool> CreateAsync(CreatePoolInput input, CancellationToken ct = default)
    {
        access.EnsureIt();

        var type = await db.AssetTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == input.TypeId, ct)
            ?? throw new DomainException("pool.type_invalid", "Choose a pooled type.");
        if (type.Mode != InventoryMode.Pooled)
            throw new DomainException("pool.type_ineligible",
                $"{type.Name} is tracked by asset tag and cannot be pooled; choose a type that is defined as a quantity pool.");
        if (!type.IsActive)
            throw new DomainException("pool.type_inactive", $"{type.Name} is inactive.");
        var sku = Normalization.Code(input.Sku);
        if (sku.Length == 0) throw new DomainException("pool.sku_required", "SKU is required.");
        if (sku.Length > SkuMaxLength)
            throw new DomainException("pool.sku_length", $"SKU may be at most {SkuMaxLength} characters.");
        var description = input.Description?.Trim() ?? "";
        if (description.Length is 0 or > 500)
            throw new DomainException("pool.description", "Description is required (at most 500 characters).");
        if (input.InitialQuantity < 0)
            throw new DomainException("pool.quantity_negative", "Initial quantity cannot be negative.");
        var minimum = ValidMinimum(input.MinimumStock);
        if (!await db.Sites.AnyAsync(s => s.Id == input.SiteId && s.IsActive, ct))
            throw new DomainException("pool.site_invalid", "Choose an active site.");
        if (await db.Pools.AnyAsync(p => p.Sku == sku && p.SiteId == input.SiteId, ct))
            throw new ConflictException("pool.duplicate", $"A pool for SKU {sku} already exists at that site.");

        var now = clock.UtcNow;
        var pool = new Pool
        {
            TypeId = type.Id, Sku = sku, SiteId = input.SiteId, Description = description,
            Available = input.InitialQuantity, CreatedAt = now, MinimumStock = minimum,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Pools.Add(pool);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("pool.duplicate", $"A pool for SKU {sku} already exists at that site.");
        }

        if (input.InitialQuantity > 0)
        {
            db.StockEvents.Add(NewEvent(pool.Id, StockEventType.Receipt, input.InitialQuantity,
                new PoolBalanceSnapshot(input.InitialQuantity, 0),
                Normalization.OptionalText(input.Reason) ?? "Initial stock", now));
        }
        audit.Record("pool.create", nameof(Pool), pool.Id, pool.SiteId, Normalization.OptionalText(input.Reason),
            new { pool.Sku, Type = type.Name, pool.Description, input.InitialQuantity, pool.MinimumStock });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return pool;
    }

    public const int MinimumStockMax = 100_000;

    private static int? ValidMinimum(int? minimum) =>
        minimum is < 0 or > MinimumStockMax
            ? throw new DomainException("pool.minimum", $"The minimum stock must be between 0 and {MinimumStockMax} (or empty for none).")
            : minimum;

    /// <summary>
    /// Sets (or clears, with null) the pool's minimum stock level. It is a setting, not a stock movement: no stock event,
    /// only an audit row with the old and new value.
    /// </summary>
    public async Task<Pool> SetMinimumAsync(int poolId, int? minimum, CancellationToken ct = default)
    {
        access.EnsureIt();
        var value = ValidMinimum(minimum);
        var pool = await db.Pools.FirstOrDefaultAsync(p => p.Id == poolId, ct) ?? throw new AccessDeniedException();
        var before = pool.MinimumStock;
        if (before == value) return pool;
        pool.MinimumStock = value;
        audit.Record("pool.minimum", nameof(Pool), pool.Id, pool.SiteId, details: new { before, after = value });
        await db.SaveChangesAsync(ct);
        return pool;
    }

    /// <summary>Records received stock: Available += quantity.</summary>
    public async Task<StockEvent> ReceiveAsync(int poolId, int quantity, string reason, CancellationToken ct = default)
    {
        access.EnsureIt();
        var why = RequireReason(reason);
        RequirePositive(quantity);
        var pool = await LoadAsync(poolId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PoolBalance.AddAvailableAsync(db, poolId, quantity, ct);
        var ev = await RecordAsync(pool, StockEventType.Receipt, quantity, why, "pool.receipt", ct);
        await tx.CommitAsync(ct);
        return ev;
    }

    /// <summary>Records lost/damaged stock: Available -= quantity. Cannot exceed what is available.</summary>
    public async Task<StockEvent> RecordLossAsync(int poolId, int quantity, string reason, CancellationToken ct = default)
    {
        access.EnsureIt();
        var why = RequireReason(reason);
        RequirePositive(quantity);
        var pool = await LoadAsync(poolId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await PoolBalance.TryRemoveAvailableAsync(db, poolId, quantity, ct))
            throw new ConflictException("pool.available_insufficient",
                $"Cannot record a loss of {quantity}: fewer units are available. Outstanding units must be returned first.");
        var ev = await RecordAsync(pool, StockEventType.Loss, -quantity, why, "pool.loss", ct);
        await tx.CommitAsync(ct);
        return ev;
    }

    /// <summary>
    /// Count adjustment: sets Available to the physically counted number of units on the shelf (a target count,
    /// not a delta). The event records the resulting signed delta. Conditional on the balance read in the same
    /// transaction, so a concurrent change yields a conflict instead of a lost update.
    /// </summary>
    public async Task<StockEvent> AdjustCountAsync(int poolId, int countedAvailable, string reason, CancellationToken ct = default)
    {
        access.EnsureIt();
        var why = RequireReason(reason);
        if (countedAvailable < 0)
            throw new DomainException("pool.quantity_negative", "Counted quantity cannot be negative.");
        var pool = await LoadAsync(poolId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await PoolBalance.ReadAsync(db, poolId, ct);
        var rows = await db.Pools.Where(p => p.Id == poolId && p.Available == current.Available)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Available, countedAvailable), ct);
        if (rows != 1)
            throw new ConflictException("pool.concurrent_change", "The pool balance changed meanwhile; review it and count again.");
        var ev = await RecordAsync(pool, StockEventType.CountAdjustment, countedAvailable - current.Available, why,
            "pool.count_adjustment", ct, new { previousAvailable = current.Available, countedAvailable });
        await tx.CommitAsync(ct);
        return ev;
    }

    /// <summary>
    /// Moves available units to the pool with the same SKU and type at another site (which must already exist).
    /// Writes linked TransferOut/TransferIn events whose <see cref="StockEvent.CounterpartEventId"/> point at each other.
    /// </summary>
    public async Task<(StockEvent Out, StockEvent In)> TransferAsync(int fromPoolId, int toPoolId, int quantity,
        string reason, CancellationToken ct = default)
    {
        access.EnsureIt();
        var why = RequireReason(reason);
        RequirePositive(quantity);
        var source = await LoadAsync(fromPoolId, ct);
        var target = await LoadAsync(toPoolId, ct);
        if (source.Id == target.Id || source.SiteId == target.SiteId)
            throw new DomainException("pool.transfer_same_site", "Transfers must go to a pool at a different site.");
        if (source.Sku != target.Sku || source.TypeId != target.TypeId)
            throw new DomainException("pool.transfer_mismatch", "Transfers must go to a pool with the same SKU and type.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Touch the two rows in id order so opposite concurrent transfers cannot deadlock.
        if (target.Id < source.Id) await PoolBalance.AddAvailableAsync(db, target.Id, quantity, ct);
        if (!await PoolBalance.TryRemoveAvailableAsync(db, source.Id, quantity, ct))
            throw new ConflictException("pool.available_insufficient",
                $"Cannot transfer {quantity}: fewer units are available at the source site.");
        if (target.Id > source.Id) await PoolBalance.AddAvailableAsync(db, target.Id, quantity, ct);

        var now = clock.UtcNow;
        var outEv = NewEvent(source.Id, StockEventType.TransferOut, -quantity, await PoolBalance.ReadAsync(db, source.Id, ct), why, now);
        var inEv = NewEvent(target.Id, StockEventType.TransferIn, quantity, await PoolBalance.ReadAsync(db, target.Id, ct), why, now);
        db.StockEvents.Add(outEv);
        await db.SaveChangesAsync(ct);

        // Ids are needed to link the pair, hence several saves inside the one transaction.
        inEv.CounterpartEventId = outEv.Id;
        db.StockEvents.Add(inEv);
        await db.SaveChangesAsync(ct);
        outEv.CounterpartEventId = inEv.Id;

        var details = new { fromPoolId = source.Id, toPoolId = target.Id, source.Sku, quantity,
            fromSiteId = source.SiteId, toSiteId = target.SiteId, outEventId = outEv.Id, inEventId = inEv.Id };
        audit.Record("pool.transfer_out", nameof(Pool), source.Id, source.SiteId, why, details);
        audit.Record("pool.transfer_in", nameof(Pool), target.Id, target.SiteId, why, details);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (outEv, inEv);
    }

    private async Task<StockEvent> RecordAsync(Pool pool, StockEventType type, int delta, string reason, string action,
        CancellationToken ct, object? extra = null)
    {
        var balance = await PoolBalance.ReadAsync(db, pool.Id, ct);
        var ev = NewEvent(pool.Id, type, delta, balance, reason, clock.UtcNow);
        db.StockEvents.Add(ev);
        audit.Record(action, nameof(Pool), pool.Id, pool.SiteId, reason, new
        {
            pool.Sku, delta, availableAfter = balance.Available, outstandingAfter = balance.Outstanding, extra,
        });
        await db.SaveChangesAsync(ct);
        return ev;
    }

    private StockEvent NewEvent(int poolId, StockEventType type, int delta, PoolBalanceSnapshot after, string reason,
        DateTimeOffset at) => new()
    {
        PoolId = poolId, Type = type, QuantityDelta = delta,
        AvailableAfter = after.Available, OutstandingAfter = after.Outstanding,
        Reason = reason, Actor = user.Name, OccurredAt = at,
    };

    private async Task<Pool> LoadAsync(int poolId, CancellationToken ct) =>
        await db.Pools.AsNoTracking().FirstOrDefaultAsync(p => p.Id == poolId, ct) ?? throw new AccessDeniedException();

    private static string RequireReason(string? reason)
    {
        var r = reason?.Trim() ?? "";
        if (r.Length == 0) throw new DomainException("pool.reason_required", "A reason is required for stock adjustments.");
        if (r.Length > ReasonMaxLength)
            throw new DomainException("pool.reason_length", $"Reason may be at most {ReasonMaxLength} characters.");
        return r;
    }

    private static void RequirePositive(int quantity)
    {
        if (quantity <= 0) throw new DomainException("pool.quantity_positive", "Quantity must be greater than zero.");
    }
}
