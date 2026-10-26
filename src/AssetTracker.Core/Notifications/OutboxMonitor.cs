using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Notifications;

public sealed record OutboxStatus(
    int PendingCount,
    int FailedCount,
    int SkippedCount,
    DateTimeOffset? OldestPendingCreatedAt,
    TimeSpan? OldestPendingAge,
    string? LastError,
    DateTimeOffset? LastErrorAt);

public sealed record OutboxRowView(
    long Id,
    NotificationKind Kind,
    NotificationStatus Status,
    string? RecipientEmail,
    string? SkipReason,
    string Subject,
    int? LoanId,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastAttemptAt,
    string? LastError);

public sealed record OutboxPage(IReadOnlyList<OutboxRowView> Rows, int Page, int PageSize, int Total)
{
    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

/// <summary>
/// Operational view of the mail outbox for IT: counts, last error, failed/skipped rows and manual requeue.
/// Every method requires the IT role (<see cref="AccessScope.EnsureIt"/>); requeues are audited.
/// </summary>
public class OutboxMonitor(AppDbContext db, AccessScope access, AuditWriter audit, IClock clock)
{
    public async Task<OutboxStatus> GetStatusAsync(CancellationToken ct = default)
    {
        access.EnsureIt();
        var counts = await db.Outbox
            .Where(x => x.Status != NotificationStatus.Sent)
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int Count(NotificationStatus s) => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0;

        var oldest = await db.Outbox.Where(x => x.Status == NotificationStatus.Pending)
            .OrderBy(x => x.CreatedAt).Select(x => (DateTimeOffset?)x.CreatedAt).FirstOrDefaultAsync(ct);

        var last = await db.Outbox
            .Where(x => x.LastError != null && x.LastAttemptAt != null
                        && (x.Status == NotificationStatus.Pending || x.Status == NotificationStatus.Failed))
            .OrderByDescending(x => x.LastAttemptAt)
            .Select(x => new { x.LastError, x.LastAttemptAt })
            .FirstOrDefaultAsync(ct);

        var now = clock.UtcNow;
        return new OutboxStatus(
            Count(NotificationStatus.Pending), Count(NotificationStatus.Failed), Count(NotificationStatus.Skipped),
            oldest, oldest is { } o ? (o <= now ? now - o : TimeSpan.Zero) : null,
            last?.LastError, last?.LastAttemptAt);
    }

    /// <summary>Rows in the given statuses (default: Failed and Skipped), newest first, server-paged (1-based).</summary>
    public async Task<OutboxPage> ListAsync(IReadOnlyCollection<NotificationStatus>? statuses = null, int page = 1,
        int pageSize = 50, CancellationToken ct = default)
    {
        access.EnsureIt();
        var wanted = (statuses is { Count: > 0 } ? statuses : [NotificationStatus.Failed, NotificationStatus.Skipped]).ToList();
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);
        var q = db.Outbox.AsNoTracking().Where(x => wanted.Contains(x.Status));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new OutboxRowView(x.Id, x.Kind, x.Status, x.RecipientEmail, x.SkipReason, x.Subject, x.LoanId,
                x.Attempts, x.CreatedAt, x.LastAttemptAt, x.LastError))
            .ToListAsync(ct);
        return new OutboxPage(rows, page, pageSize, total);
    }

    /// <summary>Resets one Failed row to Pending with zero attempts, due now. Audited.</summary>
    /// <exception cref="DomainException">The row does not exist or is not Failed.</exception>
    public async Task RequeueAsync(long id, CancellationToken ct = default)
    {
        access.EnsureIt();
        var row = await db.Outbox.FirstOrDefaultAsync(x => x.Id == id, ct)
                  ?? throw new DomainException("outbox.not-found", "Notification not found.");
        if (row.Status != NotificationStatus.Failed)
            throw new DomainException("outbox.not-failed", "Only failed notifications can be requeued.");

        row.Status = NotificationStatus.Pending;
        row.Attempts = 0;
        row.NextAttemptAt = clock.UtcNow;
        audit.Record("notification.requeue", nameof(NotificationOutbox), row.Id, null,
            details: new { row.Kind, row.RecipientEmail, row.LoanId, PreviousError = row.LastError });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Resets every Failed row to Pending with zero attempts, due now. Audited as one event.</summary>
    /// <returns>Number of rows requeued.</returns>
    public async Task<int> RequeueAllFailedAsync(CancellationToken ct = default)
    {
        access.EnsureIt();
        var now = clock.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var count = await db.Outbox.Where(x => x.Status == NotificationStatus.Failed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, NotificationStatus.Pending)
                .SetProperty(x => x.Attempts, 0)
                .SetProperty(x => x.NextAttemptAt, now), ct);
        audit.Record("notification.requeue-all-failed", nameof(NotificationOutbox), null, null,
            details: new { Count = count });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return count;
    }
}
