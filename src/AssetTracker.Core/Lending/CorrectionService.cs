using System.Data.Common;
using System.Globalization;
using System.Text.Json;
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
/// Corrected values for a loan. A null property means "unchanged".
/// <list type="bullet">
/// <item><see cref="ClearDueDate"/> removes the due date (takes precedence over <see cref="DueDate"/>).</item>
/// <item><see cref="ReturnedAt"/>: the corrected return time (<see cref="Loan.ClosedAt"/>). For a tagged loan, setting
/// it on an open loan closes it. For a pool loan it only applies when the corrected loan is fully returned.</item>
/// <item><see cref="Reopen"/>: tagged loans only — the asset was in fact not returned.</item>
/// <item><see cref="Quantity"/> / <see cref="ReturnedQuantity"/>: pool loans only.</item>
/// </list>
/// </summary>
public sealed record CorrectionRequest
{
    public int? HolderId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateOnly? DueDate { get; init; }
    public bool ClearDueDate { get; init; }
    public DateTimeOffset? ReturnedAt { get; init; }
    public bool Reopen { get; init; }
    public int? Quantity { get; init; }
    public int? ReturnedQuantity { get; init; }

    /// <summary>Required on commit.</summary>
    public string? Reason { get; init; }
}

/// <summary>One field-level change. Values are invariant strings (ISO 8601 for times/dates, ids for holders).</summary>
public sealed record FieldChange(string Field, string? From, string? To);

/// <summary>The computed effect of a correction (preview), or of a committed one.</summary>
/// <param name="SupersedesEventId">The loan's latest event, which the correction event will supersede.</param>
/// <param name="Changes">Field-level changes of the effective loan state (from → to).</param>
/// <param name="PoolAvailableBefore">Pool loans: current available units; null for tagged loans.</param>
/// <param name="PoolAvailableAfter">Pool loans: available units after the correction (may be negative in a rejected preview).</param>
/// <param name="AssetStateBefore">Tagged loans: current asset state.</param>
/// <param name="AssetStateAfter">Tagged loans: asset state after the correction.</param>
/// <param name="Conflicts">Reasons the correction cannot be committed; empty when it can.</param>
public sealed record CorrectionPreview(
    int LoanId, long SupersedesEventId, IReadOnlyList<FieldChange> Changes,
    int? PoolAvailableBefore, int? PoolAvailableAfter, int? PoolOutstandingBefore, int? PoolOutstandingAfter,
    AssetState? AssetStateBefore, AssetState? AssetStateAfter,
    int PreviousHolderId, int CorrectedHolderId, IReadOnlyList<string> Conflicts)
{
    public bool HasConflicts => Conflicts.Count > 0;
    public bool HolderChanged => PreviousHolderId != CorrectedHolderId;
}

public sealed record CorrectionResult(Loan Loan, LoanEvent Event, CorrectionPreview Effects);

/// <summary>
/// Loan corrections (WP2.2). IT only. A correction never edits or deletes an existing <see cref="LoanEvent"/>:
/// it appends a <see cref="LoanEventType.Corrected"/> event that supersedes the loan's latest event, records
/// field-level from/to in <see cref="LoanEvent.ChangeJson"/>, and updates the loan's effective columns with an
/// optimistic check on <see cref="Loan.EffectiveVersion"/>. Timeline overlaps, impossible stock balances and
/// asset-state conflicts are rejected (<see cref="ConflictException"/> listing every reason).
/// </summary>
public class CorrectionService(
    AppDbContext db, AccessScope access, AuditWriter audit, NotificationService notifications,
    ICurrentUser user, IClock clock)
{
    public const int ReasonMaxLength = 2000;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Computes the effects and conflicts of a correction without saving anything.</summary>
    public async Task<CorrectionPreview> PreviewAsync(int loanId, CorrectionRequest request, CancellationToken ct = default)
    {
        access.EnsureIt();
        var loan = await LoadAsync(loanId, ct);
        var plan = await PlanAsync(loan, request, ct);
        return plan.Preview;
    }

    public async Task<CorrectionResult> CommitAsync(int loanId, CorrectionRequest request, CancellationToken ct = default)
    {
        access.EnsureIt();
        var reason = Normalization.OptionalText(request.Reason)
            ?? throw new DomainException("correction.reason_required", "A reason is required for a correction.");
        if (reason.Length > ReasonMaxLength)
            throw new DomainException("correction.reason_length", $"Reason may be at most {ReasonMaxLength} characters.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var loan = await LoadAsync(loanId, ct);
        var plan = await PlanAsync(loan, request, ct);
        var p = plan.Preview;
        if (p.HasConflicts)
            throw new ConflictException("correction.conflict", "The correction was rejected: " + string.Join(" ", p.Conflicts));

        var n = plan.New;
        var version = loan.EffectiveVersion;
        var holderChanged = p.HolderChanged;
        var newHolder = plan.CorrectedHolder;
        int rows;
        try
        {
            rows = await db.Loans.Where(l => l.Id == loanId && l.EffectiveVersion == version)
                .ExecuteUpdateAsync(u =>
                {
                    u.SetProperty(l => l.HolderId, n.HolderId);
                    u.SetProperty(l => l.StartedAt, n.StartedAt);
                    u.SetProperty(l => l.DueDate, n.DueDate);
                    u.SetProperty(l => l.ClosedAt, n.ClosedAt);
                    u.SetProperty(l => l.Quantity, n.Quantity);
                    u.SetProperty(l => l.ReturnedQuantity, n.ReturnedQuantity);
                    u.SetProperty(l => l.EffectiveVersion, version + 1);
                    if (holderChanged)
                    {
                        u.SetProperty(l => l.HolderNameSnapshot, newHolder.DisplayName);
                        u.SetProperty(l => l.HolderEmailSnapshot, newHolder.Email);
                    }
                }, ct);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException)
        {
            // UX_Loans_OpenAsset (reopening while another loan is open) or a check constraint.
            throw new ConflictException("correction.conflict",
                "The correction conflicts with another open loan of this asset or with the loan's quantity rules.");
        }
        if (rows == 0)
            throw new ConflictException("correction.concurrent_change",
                "The loan was changed meanwhile. Reload it and preview the correction again.");

        await ApplyInventoryAsync(loan, plan, ct);

        var now = clock.UtcNow;
        var evt = new LoanEvent
        {
            LoanId = loan.Id, Type = LoanEventType.Corrected, OccurredAt = now, RecordedAt = now, Actor = user.Name,
            HolderId = n.HolderId, Quantity = n.Quantity, DueDate = n.DueDate, Reason = reason,
            SupersedesEventId = p.SupersedesEventId,
            ChangeJson = JsonSerializer.Serialize(p.Changes, Json),
        };
        db.LoanEvents.Add(evt);
        await db.SaveChangesAsync(ct);

        var previousHolder = loan.Holder;
        loan.HolderId = n.HolderId;
        loan.Holder = newHolder;
        loan.StartedAt = n.StartedAt;
        loan.DueDate = n.DueDate;
        loan.ClosedAt = n.ClosedAt;
        loan.Quantity = n.Quantity;
        loan.ReturnedQuantity = n.ReturnedQuantity;
        loan.EffectiveVersion = version + 1;
        if (holderChanged)
        {
            loan.HolderNameSnapshot = newHolder.DisplayName;
            loan.HolderEmailSnapshot = newHolder.Email;
        }

        audit.Record("loan.correct", nameof(Loan), loan.Id, loan.Asset?.SiteId ?? loan.Pool?.SiteId, reason, new
        {
            EventId = evt.Id, p.SupersedesEventId, p.Changes,
            p.PoolAvailableBefore, p.PoolAvailableAfter, p.PoolOutstandingBefore, p.PoolOutstandingAfter,
            AssetStateBefore = p.AssetStateBefore?.ToString(), AssetStateAfter = p.AssetStateAfter?.ToString(),
        });
        await notifications.QueueLoanNotificationAsync(loan, evt, NotificationKind.Correction,
            [previousHolder, newHolder], ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CorrectionResult(loan, evt, p);
    }

    /// <summary>Parses a Corrected event's <see cref="LoanEvent.ChangeJson"/>.</summary>
    public static IReadOnlyList<FieldChange> ParseChanges(string? changeJson)
    {
        if (string.IsNullOrWhiteSpace(changeJson)) return [];
        try { return JsonSerializer.Deserialize<List<FieldChange>>(changeJson, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private async Task<Loan> LoadAsync(int loanId, CancellationToken ct) =>
        await db.Loans.AsNoTracking().Include(l => l.Holder).Include(l => l.Asset).Include(l => l.Pool)
            .FirstOrDefaultAsync(l => l.Id == loanId, ct) ?? throw new AccessDeniedException();

    private sealed record State(int HolderId, DateTimeOffset StartedAt, DateOnly? DueDate, DateTimeOffset? ClosedAt,
        int Quantity, int ReturnedQuantity);

    private sealed record Plan(State Old, State New, Person CorrectedHolder, int OutstandingDelta, CorrectionPreview Preview);

    private async Task<Plan> PlanAsync(Loan loan, CorrectionRequest r, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var tagged = loan.AssetId is not null;
        var old = new State(loan.HolderId, loan.StartedAt, loan.DueDate, loan.ClosedAt, loan.Quantity, loan.ReturnedQuantity);
        var conflicts = new List<string>();

        if (r.Reopen && r.ReturnedAt is not null)
            throw new DomainException("correction.reopen_and_return", "Either reopen the loan or correct its return time, not both.");

        var holder = loan.Holder!;
        if (r.HolderId is { } hid && hid != loan.HolderId)
            holder = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == hid, ct)
                ?? throw new DomainException("correction.holder_unknown", "The corrected holder does not exist.");

        var start = r.StartedAt ?? old.StartedAt;
        var due = r.ClearDueDate ? null : r.DueDate ?? old.DueDate;
        int qty, returned;
        DateTimeOffset? closedAt;
        if (tagged)
        {
            if (r.Quantity is not null && r.Quantity != 1)
                throw new DomainException("correction.tagged_quantity", "A tagged asset loan always has quantity 1.");
            if (r.ReturnedQuantity is not null)
                throw new DomainException("correction.tagged_returned",
                    "For a tagged asset, correct the return time or reopen the loan instead of the returned quantity.");
            qty = 1;
            if (r.Reopen) closedAt = null;
            else closedAt = r.ReturnedAt ?? old.ClosedAt;
            returned = closedAt is null ? 0 : 1;
        }
        else
        {
            if (r.Reopen)
                throw new DomainException("correction.pool_reopen",
                    "To reopen a pool loan, correct its returned quantity.");
            qty = r.Quantity ?? old.Quantity;
            returned = r.ReturnedQuantity ?? old.ReturnedQuantity;
            if (qty <= 0) conflicts.Add("The checked-out quantity must be greater than zero.");
            if (returned < 0) conflicts.Add("The returned quantity cannot be negative.");
            if (returned > qty) conflicts.Add($"The returned quantity ({returned}) cannot exceed the checked-out quantity ({qty}).");
            if (qty > 0 && returned == qty)
                closedAt = r.ReturnedAt ?? old.ClosedAt ?? now;
            else
            {
                if (r.ReturnedAt is not null)
                    conflicts.Add("A return time only applies when the loan is fully returned.");
                closedAt = null;
            }
        }

        var @new = new State(holder.Id, start, due, closedAt, qty, returned);

        // Temporal validation.
        if (start > now) conflicts.Add("The start time cannot be in the future.");
        if (closedAt is { } c)
        {
            if (c > now) conflicts.Add("The return time cannot be in the future.");
            if (c < start) conflicts.Add("The return time cannot be before the start time.");
        }

        var changes = Diff(old, @new);
        if (changes.Count == 0)
            throw new DomainException("correction.no_change", "Nothing would change: enter at least one corrected value.");

        var latestEventId = await db.LoanEvents.Where(e => e.LoanId == loan.Id)
            .OrderByDescending(e => e.Id).Select(e => e.Id).FirstOrDefaultAsync(ct);

        AssetState? stateBefore = null, stateAfter = null;
        int? availBefore = null, availAfter = null, outBefore = null, outAfter = null;
        var delta = (@new.Quantity - @new.ReturnedQuantity) - (old.Quantity - old.ReturnedQuantity);

        if (tagged)
        {
            var asset = await db.Assets.AsNoTracking().FirstAsync(a => a.Id == loan.AssetId, ct);
            stateBefore = stateAfter = asset.State;
            var wasOpen = old.ClosedAt is null;
            var isOpen = closedAt is null;
            if (wasOpen && !isOpen)
            {
                if (asset.State != AssetState.CheckedOut)
                    conflicts.Add($"The asset is {asset.State}, not checked out; it cannot be marked returned by a correction.");
                stateAfter = AssetState.Available;
            }
            else if (!wasOpen && isOpen)
            {
                if (asset.State != AssetState.Available)
                    conflicts.Add($"The asset is currently {asset.State}; it must be available to reopen this loan.");
                stateAfter = AssetState.CheckedOut;
            }

            // Overlap with any other loan of the same asset (half-open intervals [start, closed or ∞)).
            var others = await db.Loans.AsNoTracking()
                .Where(l => l.AssetId == loan.AssetId && l.Id != loan.Id)
                .Select(l => new { l.Id, l.StartedAt, l.ClosedAt, l.HolderNameSnapshot }).ToListAsync(ct);
            var end = closedAt ?? DateTimeOffset.MaxValue;
            foreach (var o in others.Where(o => start < (o.ClosedAt ?? DateTimeOffset.MaxValue) && o.StartedAt < end)
                         .OrderBy(o => o.StartedAt))
            {
                conflicts.Add($"Overlaps loan #{o.Id} of the same asset ({o.HolderNameSnapshot}, " +
                              $"{Fmt(o.StartedAt)} – {(o.ClosedAt is { } oc ? Fmt(oc) : "open")}). Reconcile that loan first.");
            }
        }
        else
        {
            var balance = await PoolBalance.ReadAsync(db, loan.PoolId!.Value, ct);
            availBefore = balance.Available;
            outBefore = balance.Outstanding;
            availAfter = balance.Available - delta;
            outAfter = balance.Outstanding + delta;
            if (availAfter < 0)
                conflicts.Add($"Only {balance.Available} unit(s) are available in the pool; the correction needs {delta} more.");
            if (outAfter < 0)
                conflicts.Add("The pool's outstanding quantity would become negative.");
        }

        var preview = new CorrectionPreview(loan.Id, latestEventId, changes, availBefore, availAfter, outBefore, outAfter,
            stateBefore, stateAfter, old.HolderId, holder.Id, conflicts);
        return new Plan(old, @new, holder, delta, preview);
    }

    private async Task ApplyInventoryAsync(Loan loan, Plan plan, CancellationToken ct)
    {
        if (loan.AssetId is { } assetId)
        {
            var wasOpen = plan.Old.ClosedAt is null;
            var isOpen = plan.New.ClosedAt is null;
            if (wasOpen == isOpen) return;
            var (from, to) = isOpen
                ? (AssetState.Available, AssetState.CheckedOut)
                : (AssetState.CheckedOut, AssetState.Available);
            var rows = await db.Assets.Where(a => a.Id == assetId && a.State == from)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.State, to), ct);
            if (rows == 0)
                throw new ConflictException("correction.asset_state",
                    $"The asset is no longer {from}; it was changed meanwhile. Reload and preview again.");
            return;
        }

        var poolId = loan.PoolId!.Value;
        if (plan.OutstandingDelta > 0)
        {
            if (!await PoolBalance.TryTakeAsync(db, poolId, plan.OutstandingDelta, ct))
                throw new ConflictException("correction.pool_insufficient",
                    $"Not enough units are available in the pool to add {plan.OutstandingDelta} outstanding.");
        }
        else if (plan.OutstandingDelta < 0)
        {
            await PoolBalance.ReturnAsync(db, poolId, -plan.OutstandingDelta, ct);
        }
    }

    private static List<FieldChange> Diff(State a, State b)
    {
        var list = new List<FieldChange>();
        void Add(string field, string? from, string? to)
        {
            if (!string.Equals(from, to, StringComparison.Ordinal)) list.Add(new FieldChange(field, from, to));
        }
        Add("holderId", a.HolderId.ToString(CultureInfo.InvariantCulture), b.HolderId.ToString(CultureInfo.InvariantCulture));
        Add("startedAt", Iso(a.StartedAt), Iso(b.StartedAt));
        Add("dueDate", a.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            b.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("closedAt", Iso(a.ClosedAt), Iso(b.ClosedAt));
        Add("quantity", a.Quantity.ToString(CultureInfo.InvariantCulture), b.Quantity.ToString(CultureInfo.InvariantCulture));
        Add("returnedQuantity", a.ReturnedQuantity.ToString(CultureInfo.InvariantCulture),
            b.ReturnedQuantity.ToString(CultureInfo.InvariantCulture));
        return list;
    }

    private static string? Iso(DateTimeOffset? t) => t?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string Fmt(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
