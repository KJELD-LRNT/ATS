using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Lending;

/// <summary>
/// Checkout target: exactly one of <see cref="AssetId"/> (tagged, quantity 1) or <see cref="PoolId"/> (+ quantity).
/// </summary>
public sealed record CheckoutRequest(
    int HolderId, int? AssetId = null, int? PoolId = null, int Quantity = 1,
    DateOnly? DueDate = null, string? Condition = null, string? Note = null);

/// <param name="DirectoryStale">The people snapshot was stale at checkout (recorded, not blocking): warn IT.</param>
public sealed record CheckoutResult(Loan Loan, LoanEvent Event, bool DirectoryStale);

public sealed record ReturnResult(Loan Loan, LoanEvent Event, bool FullyReturned);

/// <summary>A person row for the IT-only holder picker.</summary>
public sealed record PersonOption(int Id, string DisplayName, string Username, string? Email, bool IsActive,
    string? SiteKey, bool CanReceiveNewLoan);

/// <summary>
/// Checkout and return of tagged assets and pool quantities (WP2.1). IT only. Each operation runs in one explicit
/// transaction covering the inventory state change (conditional atomic update), the loan and its
/// <see cref="LoanEvent"/>, the audit row and the notification outbox rows.
/// </summary>
public class LendingService(
    AppDbContext db, AccessScope access, AuditWriter audit, NotificationService notifications,
    IDirectoryFreshness freshness, ICurrentUser user, IClock clock)
{
    public const int ConditionMaxLength = 200;
    public const int NoteMaxLength = 2000;

    /// <summary>
    /// IT-only people search by name, username or email. With <paramref name="eligibleOnly"/> only active people
    /// with a valid email address are returned (new loans); otherwise everyone (e.g. corrections, inactive holders).
    /// </summary>
    public async Task<IReadOnlyList<PersonOption>> SearchPeopleAsync(string? term, bool eligibleOnly = true, int take = 20,
        CancellationToken ct = default)
    {
        access.EnsureIt();
        var t = term?.Trim();
        if (string.IsNullOrEmpty(t)) return [];
        var pattern = Paging.ContainsPattern(t);
        var q = db.People.AsNoTracking().Where(p =>
            EF.Functions.Like(p.DisplayName, pattern, @"\") ||
            EF.Functions.Like(p.Username, pattern, @"\") ||
            (p.Email != null && EF.Functions.Like(p.Email, pattern, @"\")));
        if (eligibleOnly) q = q.Where(p => p.IsActive && p.Email != null && p.Email != "");
        var people = await q.OrderBy(p => p.DisplayName).ThenBy(p => p.Id).Take(Math.Clamp(take, 1, 100) * 2)
            .Select(p => new { p.Id, p.DisplayName, p.Username, p.Email, p.IsActive, SiteKey = p.Site != null ? p.Site.Key : null })
            .ToListAsync(ct);
        return people
            .Select(p => new PersonOption(p.Id, p.DisplayName, p.Username, p.Email, p.IsActive, p.SiteKey,
                p.IsActive && EmailAddresses.IsValid(p.Email)))
            .Where(p => !eligibleOnly || p.CanReceiveNewLoan)
            .Take(Math.Clamp(take, 1, 100)).ToList();
    }

    /// <summary>IT-only lookup of one person (any state) for pickers; null when unknown.</summary>
    public async Task<PersonOption?> GetPersonAsync(int id, CancellationToken ct = default)
    {
        access.EnsureIt();
        var p = await db.People.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Id, x.DisplayName, x.Username, x.Email, x.IsActive, SiteKey = x.Site != null ? x.Site.Key : null })
            .FirstOrDefaultAsync(ct);
        return p is null ? null : new PersonOption(p.Id, p.DisplayName, p.Username, p.Email, p.IsActive, p.SiteKey,
            p.IsActive && EmailAddresses.IsValid(p.Email));
    }

    /// <summary>Whether the directory snapshot is currently stale (for the checkout warning banner).</summary>
    public Task<bool> IsDirectoryStaleAsync(CancellationToken ct = default) => freshness.IsStaleAsync(ct);

    public async Task<CheckoutResult> CheckoutAsync(CheckoutRequest r, CancellationToken ct = default)
    {
        access.EnsureIt();
        if ((r.AssetId is null) == (r.PoolId is null))
            throw new DomainException("loan.target", "Choose either a tagged asset or a pool (not both).");

        var holder = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.HolderId, ct)
            ?? throw new DomainException("loan.holder_unknown", "Choose the person who receives the item.");
        if (!holder.IsActive)
            throw new DomainException("loan.holder_inactive",
                $"{holder.DisplayName} is inactive in the directory and cannot receive new loans.");
        var email = EmailAddresses.Normalize(holder.Email)
            ?? throw new DomainException("loan.holder_no_email",
                $"{holder.DisplayName} has no valid email address in the directory; a missing email blocks checkout.");
        if (r.DueDate is { } due && due < clock.LocalToday)
            throw new DomainException("loan.due_past", "The due date cannot be in the past.");
        var condition = Limit(Normalization.OptionalText(r.Condition), ConditionMaxLength, "Condition");
        var note = Limit(Normalization.OptionalText(r.Note), NoteMaxLength, "Note");

        IndividualAsset? asset = null;
        Pool? pool = null;
        int quantity;
        if (r.AssetId is { } assetId)
        {
            if (r.Quantity != 1)
                throw new DomainException("loan.tagged_quantity", "A tagged asset is always checked out as quantity 1.");
            quantity = 1;
            asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct)
                ?? throw new AccessDeniedException();
            if (asset.State != AssetState.Available)
                throw new ConflictException("loan.asset_unavailable",
                    $"Asset {asset.Tag} is not available (current state: {asset.State}).");
        }
        else
        {
            if (r.Quantity <= 0)
                throw new DomainException("loan.quantity_positive", "Quantity must be greater than zero.");
            quantity = r.Quantity;
            pool = await db.Pools.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.PoolId, ct)
                ?? throw new AccessDeniedException();
        }

        var stale = await freshness.IsStaleAsync(ct);
        var now = clock.UtcNow;
        var siteId = asset?.SiteId ?? pool!.SiteId;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (asset is not null)
        {
            var rows = await db.Assets.Where(a => a.Id == asset.Id && a.State == AssetState.Available)
                .ExecuteUpdateAsync(u =>
                {
                    u.SetProperty(a => a.State, AssetState.CheckedOut);
                    if (condition is not null) u.SetProperty(a => a.Condition, condition);
                }, ct);
            if (rows == 0)
                throw new ConflictException("loan.asset_unavailable",
                    $"Asset {asset.Tag} is not available: it was checked out or changed meanwhile.");
        }
        else if (!await PoolBalance.TryTakeAsync(db, pool!.Id, quantity, ct))
        {
            var balance = await PoolBalance.ReadAsync(db, pool.Id, ct);
            throw new ConflictException("loan.pool_insufficient",
                $"Only {balance.Available} unit(s) of {pool.Sku} are available; cannot check out {quantity}.");
        }

        var loan = new Loan
        {
            AssetId = asset?.Id, PoolId = pool?.Id, HolderId = holder.Id, Quantity = quantity, StartedAt = now,
            DueDate = r.DueDate, CheckoutCondition = condition, CheckoutNote = note,
            HolderNameSnapshot = holder.DisplayName, HolderEmailSnapshot = email, CreatedBy = user.Name,
        };
        var evt = new LoanEvent
        {
            Loan = loan, Type = LoanEventType.CheckedOut, OccurredAt = now, RecordedAt = now, Actor = user.Name,
            HolderId = holder.Id, Quantity = quantity, DueDate = r.DueDate, Condition = condition, Note = note,
            DirectoryStale = stale,
        };
        loan.Events.Add(evt);
        db.Loans.Add(loan);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // UX_Loans_OpenAsset: another open loan for this asset won the race.
            throw new ConflictException("loan.asset_unavailable", "This asset already has an open loan.");
        }

        audit.Record("loan.checkout", nameof(Loan), loan.Id, siteId, details: new
        {
            loan.AssetId, AssetTag = asset?.Tag, loan.PoolId, PoolSku = pool?.Sku, loan.Quantity, loan.HolderId,
            Holder = holder.DisplayName, loan.DueDate, EventId = evt.Id, DirectoryStale = stale,
        });
        await notifications.QueueLoanNotificationAsync(loan, evt, NotificationKind.Checkout, [holder], ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CheckoutResult(loan, evt, stale);
    }

    /// <summary>
    /// Records a full or partial return. Tagged loans: quantity 1, the asset becomes Available (or Unavailable when
    /// <paramref name="markUnavailable"/>). Pool loans: 1..outstanding units; the loan closes when nothing remains
    /// outstanding, otherwise it stays open (and still overdue if past due). Returning for an inactive holder is
    /// never blocked. <paramref name="returnedAt"/> defaults to now and may be neither in the future nor before the
    /// loan started. Concurrent double returns are rejected by a conditional update on the loan's effective version.
    /// </summary>
    public async Task<ReturnResult> ReturnAsync(int loanId, int? quantity = null, DateTimeOffset? returnedAt = null,
        string? condition = null, string? note = null, bool markUnavailable = false, CancellationToken ct = default)
    {
        access.EnsureIt();
        var loan = await db.Loans.AsNoTracking().Include(l => l.Holder).Include(l => l.Asset).Include(l => l.Pool)
            .FirstOrDefaultAsync(l => l.Id == loanId, ct) ?? throw new AccessDeniedException();
        if (!loan.IsOpen)
            throw new ConflictException("loan.closed", "This loan has already been fully returned.");

        var outstanding = loan.OutstandingQuantity;
        var q = quantity ?? outstanding;
        if (loan.AssetId is not null && q != 1)
            throw new DomainException("loan.tagged_quantity", "A tagged asset is returned as quantity 1.");
        if (q <= 0 || q > outstanding)
            throw new DomainException("loan.return_quantity",
                $"Return quantity must be between 1 and the {outstanding} unit(s) still outstanding.");
        if (markUnavailable && loan.AssetId is null)
            throw new DomainException("loan.unavailable_pool", "Only a tagged asset can be marked unavailable on return.");

        var now = clock.UtcNow;
        var at = returnedAt ?? now;
        if (at > now) throw new DomainException("loan.return_future", "The return time cannot be in the future.");
        if (at < loan.StartedAt)
            throw new DomainException("loan.return_before_start", "The return time cannot be before the loan started.");
        var cond = Limit(Normalization.OptionalText(condition), ConditionMaxLength, "Condition");
        var why = Limit(Normalization.OptionalText(note), NoteMaxLength, "Note");

        var newReturned = loan.ReturnedQuantity + q;
        var full = newReturned == loan.Quantity;
        var version = loan.EffectiveVersion;
        var previousReturned = loan.ReturnedQuantity;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Loans
            .Where(l => l.Id == loanId && l.EffectiveVersion == version && l.ClosedAt == null &&
                        l.ReturnedQuantity == previousReturned)
            .ExecuteUpdateAsync(u =>
            {
                u.SetProperty(l => l.ReturnedQuantity, newReturned);
                u.SetProperty(l => l.EffectiveVersion, version + 1);
                if (full) u.SetProperty(l => l.ClosedAt, at);
                if (cond is not null) u.SetProperty(l => l.ReturnCondition, cond);
                if (why is not null) u.SetProperty(l => l.ReturnNote, why);
            }, ct);
        if (rows == 0)
            throw new ConflictException("loan.concurrent_change",
                "The loan was returned or corrected meanwhile. Reload it and try again.");

        if (loan.AssetId is { } assetId)
        {
            var target = markUnavailable ? AssetState.Unavailable : AssetState.Available;
            var assetRows = await db.Assets.Where(a => a.Id == assetId && a.State == AssetState.CheckedOut)
                .ExecuteUpdateAsync(u =>
                {
                    u.SetProperty(a => a.State, target);
                    if (cond is not null) u.SetProperty(a => a.Condition, cond);
                }, ct);
            if (assetRows == 0)
                throw new ConflictException("loan.asset_state",
                    "The asset is not in the checked-out state; it was changed meanwhile.");
        }
        else
        {
            await PoolBalance.ReturnAsync(db, loan.PoolId!.Value, q, ct);
        }

        var evt = new LoanEvent
        {
            LoanId = loan.Id, Type = full ? LoanEventType.Returned : LoanEventType.PartiallyReturned,
            OccurredAt = at, RecordedAt = now, Actor = user.Name, HolderId = loan.HolderId, Quantity = q,
            DueDate = loan.DueDate, Condition = cond, Note = why,
        };
        db.LoanEvents.Add(evt);
        await db.SaveChangesAsync(ct);

        // Reflect the effective state on the (untracked) loan for the result and the notification body.
        loan.ReturnedQuantity = newReturned;
        loan.EffectiveVersion = version + 1;
        if (full) loan.ClosedAt = at;
        if (cond is not null) loan.ReturnCondition = cond;
        if (why is not null) loan.ReturnNote = why;

        audit.Record("loan.return", nameof(Loan), loan.Id, loan.Asset?.SiteId ?? loan.Pool?.SiteId, details: new
        {
            loan.AssetId, loan.PoolId, Quantity = q, Remaining = loan.OutstandingQuantity, FullyReturned = full,
            ReturnedAt = at, MarkUnavailable = markUnavailable, loan.HolderId, EventId = evt.Id,
        });
        await notifications.QueueLoanNotificationAsync(loan, evt, NotificationKind.Return, [loan.Holder], ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ReturnResult(loan, evt, full);
    }

    private static string? Limit(string? value, int max, string field) =>
        value is not null && value.Length > max
            ? throw new DomainException("loan.field_length", $"{field} may be at most {max} characters.")
            : value;
}
