using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetTracker.Core.Notifications;

/// <summary>A site whose local time had reached the reminder time in a run, with its local date.</summary>
public sealed record ReminderSiteDay(int SiteId, DateOnly LocalDate);

/// <param name="DueSites">Sites whose local time had reached <see cref="NotificationOptions.ReminderTimeOfDay"/>.</param>
public sealed record OverdueRunResult(IReadOnlyList<ReminderSiteDay> DueSites, int OverdueLoans, int Queued,
    int AlreadyQueued, int Skipped);

/// <summary>
/// Overdue reminders, per site (WP4.2). The worker calls <see cref="QueueRemindersAsync"/> every
/// <see cref="NotificationOptions.ReminderPollInterval"/>. Each call looks at every site's local time
/// (<see cref="SiteCalendar"/>) and, for each site whose local time has reached
/// <see cref="NotificationOptions.ReminderTimeOfDay"/>, queues one reminder per open, dated assignment that is
/// overdue on that site's local day (<see cref="OverduePolicy"/>, D4: from the due date itself) to the holder (only
/// when active with a valid address) and to the IT mailbox. The loan's site is the asset's current site or the
/// pool's site. Undated loans never get reminders; a partially returned pool loan gets one reminder (not one per unit).
/// <para>
/// Idempotent per site-local day: <c>DedupeKey = overdue:{loanId}:{site-local yyyy-MM-dd}:{recipient}</c> is unique
/// in the database. Existing keys are pre-checked, and a concurrent run's unique-index violation is tolerated row by
/// row, so ticks, reruns and restarts never double-queue.
/// </para>
/// Requires no role (runs in the worker as the system user). Writes one summary audit event per call that queued
/// or skipped at least one row; idle ticks write nothing.
/// </summary>
public class OverdueReminderService(AppDbContext db, NotificationService notifications, AuditWriter audit, IClock clock,
    SiteCalendar calendar, IOptionsMonitor<NotificationOptions> options, ILogger<OverdueReminderService> logger)
{
    public const string RunAuditAction = "notification.overdue-run";
    private const int LoanBatchSize = 200;

    public async Task<OverdueRunResult> QueueRemindersAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var dueSites = (await calendar.AllSitesAsync(ct)).Where(s => ReminderSchedule.IsDue(s, o.ReminderTimeOfDay)).ToList();
        var siteDays = dueSites.Select(s => new ReminderSiteDay(s.SiteId, s.Today)).ToList();
        if (dueSites.Count == 0) return new OverdueRunResult(siteDays, 0, 0, 0, 0);

        var siteToday = dueSites.ToDictionary(s => s.SiteId, s => s.Today);
        var overduePredicate = SiteCalendar.OverduePredicate(dueSites);
        var it = await notifications.GetItMailboxAsync(ct);
        var includePhone = o.IncludePhoneNumberInMail;

        int overdue = 0, queued = 0, already = 0, skipped = 0;
        var lastId = 0;
        while (true)
        {
            var loans = await db.Loans.AsNoTracking()
                .Where(l => l.ClosedAt == null && l.Id > lastId)
                .Where(overduePredicate)
                .OrderBy(l => l.Id)
                .Take(LoanBatchSize)
                .Include(l => l.Holder)
                .Include(l => l.Asset).ThenInclude(a => a!.Site)
                .Include(l => l.Pool).ThenInclude(p => p!.Site)
                .ToListAsync(ct);
            if (loans.Count == 0) break;
            lastId = loans[^1].Id;
            overdue += loans.Count;

            var rows = new List<NotificationOutbox>();
            var now = clock.UtcNow;
            foreach (var loan in loans)
                rows.AddRange(BuildReminders(loan, siteToday[loan.Asset?.SiteId ?? loan.Pool!.SiteId], it, includePhone, now));

            var keys = rows.Select(r => r.DedupeKey).ToList();
            var existing = (await db.Outbox.Where(x => keys.Contains(x.DedupeKey)).Select(x => x.DedupeKey).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            var fresh = rows.Where(r => !existing.Contains(r.DedupeKey)).ToList();
            already += rows.Count - fresh.Count;

            var inserted = await InsertTolerantAsync(fresh, ct);
            queued += inserted.Count(r => r.Status == NotificationStatus.Pending);
            skipped += inserted.Count(r => r.Status == NotificationStatus.Skipped);
            already += fresh.Count - inserted.Count;
        }

        if (queued + skipped > 0)
        {
            // Only ticks that added rows are audited, so the polling job does not flood the audit log.
            var dates = siteDays.Select(d => d.LocalDate).Distinct().Order().Select(d => d.ToString("yyyy-MM-dd")).ToList();
            audit.Record(RunAuditAction, "OverdueReminderRun", clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), null,
                details: new
                {
                    LocalDates = dates, Sites = siteDays.Select(d => d.SiteId).ToList(), OverdueLoans = overdue,
                    Queued = queued, AlreadyQueued = already, Skipped = skipped,
                });
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Overdue reminders for {Sites} site(s): {Loans} overdue loans, {Queued} queued, {Already} already queued, {Skipped} skipped",
                siteDays.Count, overdue, queued, already, skipped);
        }
        return new OverdueRunResult(siteDays, overdue, queued, already, skipped);
    }

    private static IEnumerable<NotificationOutbox> BuildReminders(Loan loan, DateOnly today, ItMailbox it,
        bool includePhone, DateTimeOffset now)
    {
        var date = today.ToString("yyyy-MM-dd");
        var facts = LoanMailFacts.From(loan, loan.Asset, loan.Pool, loan.Asset?.Site ?? loan.Pool?.Site, includePhone);
        var prefix = $"overdue:{loan.Id}:{date}:";
        var notes = new List<string>();
        string? holderAddress = null;
        if (loan.Holder is { } h)
        {
            var reason = NotificationService.HolderSkipReason(h);
            if (reason is null) holderAddress = EmailAddresses.Normalize(h.Email);
            else notes.Add($"{h.DisplayName} was not emailed: {reason}. Only IT receives this reminder.");
        }

        if (holderAddress is not null && !string.Equals(holderAddress, it.Address, StringComparison.OrdinalIgnoreCase))
        {
            var mail = NotificationTemplates.OverdueReminder(facts, new MailAudience(false, loan.Holder!.DisplayName, []), today);
            yield return Row(prefix + holderAddress.ToLowerInvariant(), holderAddress, null, mail, loan.Id, now);
        }

        var itMail = NotificationTemplates.OverdueReminder(facts, new MailAudience(true, null, notes), today);
        yield return it.Address is null
            ? Row(prefix + "it", null, it.SkipReason, itMail, loan.Id, now)
            : Row(prefix + it.Address.ToLowerInvariant(), it.Address, null, itMail, loan.Id, now);
    }

    private static NotificationOutbox Row(string key, string? email, string? skipReason, ComposedMail mail, int loanId,
        DateTimeOffset now)
    {
        var row = NotificationService.NewRow(NotificationKind.OverdueReminder, key, email, skipReason, mail, now);
        row.LoanId = loanId;
        return row;
    }

    /// <summary>
    /// Inserts rows in one save; if that hits the unique DedupeKey index (a concurrent run won the race), falls back
    /// to row-by-row inserts and ignores rows whose key now exists.
    /// </summary>
    private async Task<List<NotificationOutbox>> InsertTolerantAsync(List<NotificationOutbox> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        db.Outbox.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(ct);
            return rows;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }

        var inserted = new List<NotificationOutbox>();
        foreach (var row in rows)
        {
            row.Id = 0;
            db.Outbox.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                inserted.Add(row);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                // Queued by a concurrent run is exactly what dedupe is for; anything else is a real error.
                if (!await db.Outbox.AnyAsync(x => x.DedupeKey == row.DedupeKey, ct)) throw;
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        return inserted;
    }
}
