using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetTracker.Core.Notifications;

public sealed record OverdueRunResult(DateOnly LocalDate, int OverdueLoans, int Queued, int AlreadyQueued, int Skipped);

/// <summary>
/// Daily overdue reminders. For the client-local day (<see cref="IClock.LocalToday"/>) it queues one reminder per
/// open, dated, overdue assignment (see <see cref="OverduePolicy"/>) to the holder (only when active with a valid
/// address) and to the IT mailbox. Undated loans never get reminders; a partially returned pool loan still gets
/// one reminder (not one per unit).
/// <para>
/// Idempotent per day: <c>DedupeKey = overdue:{loanId}:{yyyy-MM-dd}:{recipient}</c> is unique in the database.
/// Existing keys are pre-checked, and a concurrent run's unique-index violation is tolerated row by row, so
/// reruns and restarts never double-queue.
/// </para>
/// Requires no role (runs in the worker as the system user). Writes one summary audit event per run.
/// </summary>
public class OverdueReminderService(AppDbContext db, NotificationService notifications, AuditWriter audit, IClock clock,
    IOptions<NotificationOptions> options, ILogger<OverdueReminderService> logger)
{
    public const string RunAuditAction = "notification.overdue-run";
    private const int LoanBatchSize = 200;

    public async Task<OverdueRunResult> QueueRemindersAsync(CancellationToken ct)
    {
        var today = clock.LocalToday;
        var date = today.ToString("yyyy-MM-dd");
        var lastDue = OverduePolicy.LatestOverdueDueDate(today);
        var it = await notifications.GetItMailboxAsync(ct);
        var includePhone = options.Value.IncludePhoneNumberInMail;

        int overdue = 0, queued = 0, already = 0, skipped = 0;
        var lastId = 0;
        while (true)
        {
            var loans = await db.Loans.AsNoTracking()
                .Where(l => l.ClosedAt == null && l.DueDate != null && l.DueDate <= lastDue && l.Id > lastId)
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
                rows.AddRange(BuildReminders(loan, today, date, it, includePhone, now));

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

        audit.Record(RunAuditAction, "OverdueReminderRun", date, null,
            details: new { LocalDate = date, OverdueLoans = overdue, Queued = queued, AlreadyQueued = already, Skipped = skipped });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Overdue reminders for {Date}: {Loans} overdue loans, {Queued} queued, {Already} already queued, {Skipped} skipped",
            date, overdue, queued, already, skipped);
        return new OverdueRunResult(today, overdue, queued, already, skipped);
    }

    /// <summary>True when a reminder run has completed for the given client-local date (used for start-up catch-up).</summary>
    public Task<bool> HasRunForAsync(DateOnly localDate, CancellationToken ct = default)
    {
        var id = localDate.ToString("yyyy-MM-dd");
        return db.AuditEvents.AnyAsync(a => a.Action == RunAuditAction && a.EntityId == id, ct);
    }

    private static IEnumerable<NotificationOutbox> BuildReminders(Loan loan, DateOnly today, string date, ItMailbox it,
        bool includePhone, DateTimeOffset now)
    {
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
