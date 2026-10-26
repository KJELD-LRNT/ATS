using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AssetTracker.Core.Notifications;

/// <summary>Resolved IT mailbox setting: either a valid address or the reason it cannot be used.</summary>
public sealed record ItMailbox(string? Address, string? SkipReason);

/// <summary>
/// Stages recipient-specific <see cref="NotificationOutbox"/> rows for loan changes. It never sends mail and
/// never calls SaveChanges: the caller saves the rows in the same transaction as the loan/event/audit, so a
/// notice exists if and only if the business change committed. The worker's <see cref="OutboxSender"/> delivers.
/// <para>Requires no role: the caller (checkout/return/correction service) has already authorized the change.</para>
/// </summary>
public class NotificationService(AppDbContext db, IClock clock, IOptions<NotificationOptions> options)
{
    public const string ItMailboxNotConfigured = "IT mailbox not configured";
    public const string ItMailboxInvalid = "IT mailbox setting is not a valid email address";

    /// <summary>
    /// Stages one outbox row per distinct recipient for a checkout, return (full or partial) or correction.
    /// <list type="bullet">
    /// <item>Recipients: every non-null person in <paramref name="holders"/> plus the IT mailbox
    /// (<see cref="AppSetting.ItMailbox"/>), deduplicated case-insensitively by address (and holders by person id).
    /// When a holder's address equals the IT mailbox, one IT row is staged.</item>
    /// <item>A holder who is inactive or has a missing/invalid address gets a row with
    /// <see cref="NotificationStatus.Skipped"/>, <c>RecipientEmail = null</c> and a <c>SkipReason</c>, so IT can see
    /// it on the ops page; the reason is also included in the IT copy. IT is still notified.</item>
    /// <item>If the IT mailbox is not configured (or invalid) a Skipped row with that reason is staged instead.</item>
    /// </list>
    /// What to pass:
    /// <list type="bullet">
    /// <item>Checkout: <c>kind = Checkout</c>, <paramref name="evt"/> = the CheckedOut event, holders = <c>[holder]</c>.
    /// (Checkout must already have rejected a holder without a valid email; see <see cref="EmailAddresses"/>.)</item>
    /// <item>Return / partial return: <c>kind = Return</c>, evt = the Returned/PartiallyReturned event
    /// (its <c>Quantity</c> = units returned now), holders = <c>[holder]</c>. Call after updating
    /// <c>loan.ReturnedQuantity</c> so the remaining quantity is right.</item>
    /// <item>Correction: <c>kind = Correction</c>, evt = the superseding Corrected event (never the original),
    /// holders = <c>[previousHolder, correctedHolder]</c> (same person twice is fine). Update the loan's effective
    /// state first; the body shows the corrected state, the event's <c>Reason</c>, and any holder whose id differs
    /// from <c>loan.HolderId</c> as "Previous holder".</item>
    /// </list>
    /// <paramref name="loan"/> and <paramref name="evt"/> may be new (unsaved): rows reference them through
    /// navigations so <c>LoanId</c>/<c>LoanEventId</c> are filled on SaveChanges. Asset/pool/site/holder navigations
    /// are loaded if not already set. Calling twice for the same event and kind in one unit of work returns the
    /// rows already staged instead of adding duplicates. <c>DedupeKey</c> = <c>{kind}:{eventKey}:{recipient}</c>,
    /// where eventKey is <c>e{evt.Id}</c> for a saved event, otherwise a fresh GUID (ids are unknown before save).
    /// </summary>
    /// <returns>The staged rows (one per distinct recipient, including skipped ones).</returns>
    public async Task<IReadOnlyList<NotificationOutbox>> QueueLoanNotificationAsync(
        Loan loan, LoanEvent evt, NotificationKind kind, IEnumerable<Person?> holders, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loan);
        ArgumentNullException.ThrowIfNull(evt);
        if (kind is not (NotificationKind.Checkout or NotificationKind.Return or NotificationKind.Correction))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Use Checkout, Return or Correction.");

        var already = db.ChangeTracker.Entries<NotificationOutbox>()
            .Where(e => e.State == EntityState.Added && e.Entity.Kind == kind && ReferenceEquals(e.Entity.LoanEvent, evt))
            .Select(e => e.Entity).ToList();
        if (already.Count > 0) return already;

        var facts = await BuildFactsAsync(loan, ct);
        var it = await GetItMailboxAsync(ct);
        var people = holders.Where(p => p is not null).Select(p => p!).DistinctBy(p => p.Id).ToList();
        var previousHolders = people.Where(p => p.Id != loan.HolderId).Select(p => p.DisplayName).ToList();

        // Plan recipients: holders first, then IT; dedupe by lower-cased address.
        var itNotes = new List<string>();
        var plans = new List<Plan>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itKey = it.Address?.ToLowerInvariant();
        foreach (var p in people)
        {
            var reason = HolderSkipReason(p);
            if (reason is not null)
            {
                itNotes.Add($"{p.DisplayName} was not emailed: {reason}.");
                plans.Add(new Plan(null, $"person-{p.Id}", p.DisplayName, IsIt: false, reason));
                continue;
            }
            var address = EmailAddresses.Normalize(p.Email)!;
            var key = address.ToLowerInvariant();
            if (key == itKey || !seen.Add(key)) continue;
            plans.Add(new Plan(address, key, p.DisplayName, IsIt: false, null));
        }
        plans.Add(it.Address is null
            ? new Plan(null, "it", null, IsIt: true, it.SkipReason)
            : new Plan(it.Address, itKey!, null, IsIt: true, null));

        var eventKey = evt.Id > 0 ? $"e{evt.Id}" : $"n{Guid.NewGuid():N}";
        var now = clock.UtcNow;
        var rows = new List<NotificationOutbox>();
        foreach (var plan in plans)
        {
            var audience = new MailAudience(plan.IsIt, plan.Name, plan.IsIt ? itNotes : []);
            var mail = kind switch
            {
                NotificationKind.Checkout => NotificationTemplates.Checkout(facts, audience),
                NotificationKind.Return => NotificationTemplates.Return(facts, audience, evt.Quantity,
                    partial: loan.OutstandingQuantity > 0),
                _ => NotificationTemplates.Correction(facts, audience, evt.Reason, previousHolders),
            };
            var row = NewRow(kind, $"{KindKey(kind)}:{eventKey}:{plan.KeyPart}", plan.Email, plan.SkipReason, mail, now);
            if (loan.Id > 0) row.LoanId = loan.Id; else row.Loan = loan;
            if (evt.Id > 0) row.LoanEventId = evt.Id; else row.LoanEvent = evt;
            db.Outbox.Add(row);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Stages one <see cref="NotificationKind.SystemAlert"/> row for the IT mailbox (e.g. a failed directory sync).
    /// Like loan notices it never sends and never saves: the caller saves it with the change that raised the alert.
    /// When the IT mailbox is not configured (or invalid) the row is <see cref="NotificationStatus.Skipped"/> with
    /// that reason, so the alert is still visible on the mail ops page. A row with the same
    /// <paramref name="dedupeKey"/> that is already staged or stored is returned instead of adding a duplicate.
    /// The body must not contain stack traces or secrets; the caller sanitizes it.
    /// </summary>
    public async Task<NotificationOutbox> QueueSystemAlertAsync(string subject, string body, string dedupeKey,
        CancellationToken ct = default)
    {
        var staged = db.ChangeTracker.Entries<NotificationOutbox>()
            .FirstOrDefault(e => e.State == EntityState.Added && e.Entity.DedupeKey == dedupeKey)?.Entity;
        if (staged is not null) return staged;
        var stored = await db.Outbox.FirstOrDefaultAsync(x => x.DedupeKey == dedupeKey, ct);
        if (stored is not null) return stored;

        var it = await GetItMailboxAsync(ct);
        var row = NewRow(NotificationKind.SystemAlert, dedupeKey, it.Address, it.Address is null ? it.SkipReason : null,
            new ComposedMail(subject.Length <= 500 ? subject : subject[..500], body), clock.UtcNow);
        db.Outbox.Add(row);
        return row;
    }

    /// <summary>Reads <see cref="AppSetting.ItMailbox"/> and validates it.</summary>
    public async Task<ItMailbox> GetItMailboxAsync(CancellationToken ct = default)
    {
        var setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == AppSetting.ItMailbox, ct);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value)) return new ItMailbox(null, ItMailboxNotConfigured);
        var address = EmailAddresses.Normalize(setting.Value);
        return address is null ? new ItMailbox(null, ItMailboxInvalid) : new ItMailbox(address, null);
    }

    /// <summary>
    /// Why a holder cannot be emailed, or null when they can: inactive in the directory, or a missing/invalid address.
    /// </summary>
    public static string? HolderSkipReason(Person p)
    {
        if (!p.IsActive) return "holder is inactive in the directory";
        if (string.IsNullOrWhiteSpace(p.Email)) return "holder has no email address in the directory";
        if (!EmailAddresses.IsValid(p.Email)) return "holder's email address is invalid";
        return null;
    }

    /// <summary>Loads (if needed) the asset/pool, site and holder of a loan and builds the mail facts.</summary>
    public async Task<LoanMailFacts> BuildFactsAsync(Loan loan, CancellationToken ct = default)
    {
        var asset = loan.Asset ?? (loan.AssetId is { } aid ? await db.Assets.FindAsync([aid], ct) : null);
        var pool = asset is null ? loan.Pool ?? (loan.PoolId is { } pid ? await db.Pools.FindAsync([pid], ct) : null) : null;
        var site = asset?.Site ?? pool?.Site;
        var siteId = asset?.SiteId ?? pool?.SiteId;
        if (site is null && siteId is { } sid) site = await db.Sites.FindAsync([sid], ct);
        if (string.IsNullOrWhiteSpace(loan.HolderNameSnapshot) && loan.Holder is null && loan.HolderId > 0)
            await db.People.FindAsync([loan.HolderId], ct);
        return LoanMailFacts.From(loan, asset, pool, site, options.Value.IncludePhoneNumberInMail);
    }

    internal static NotificationOutbox NewRow(NotificationKind kind, string dedupeKey, string? email, string? skipReason,
        ComposedMail mail, DateTimeOffset now) => new()
    {
        Kind = kind,
        Status = skipReason is null ? NotificationStatus.Pending : NotificationStatus.Skipped,
        DedupeKey = dedupeKey.Length <= 400 ? dedupeKey : dedupeKey[..400],
        RecipientEmail = skipReason is null ? email : null,
        SkipReason = skipReason is null || skipReason.Length <= 500 ? skipReason : skipReason[..500],
        Subject = mail.Subject,
        Body = mail.Body,
        CreatedAt = now,
        NextAttemptAt = now,
    };

    internal static string KindKey(NotificationKind kind) => kind switch
    {
        NotificationKind.Checkout => "checkout",
        NotificationKind.Return => "return",
        NotificationKind.Correction => "correction",
        NotificationKind.OverdueReminder => "overdue",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private sealed record Plan(string? Email, string KeyPart, string? Name, bool IsIt, string? SkipReason);
}
