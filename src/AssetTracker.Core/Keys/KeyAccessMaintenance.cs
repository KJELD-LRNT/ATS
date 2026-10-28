using System.Data;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AssetTracker.Core.Keys;

/// <summary>Worker-only maintenance: expiry requests revocation; it never claims to have changed hardware.</summary>
public class KeyAccessMaintenance(AppDbContext db, IClock clock, AuditWriter audit, SiteCalendar calendar,
    IOptionsMonitor<NotificationOptions> options)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        await ExpireAccessAsync(ct);
        await QueueRemindersAsync(ct);
    }

    private async Task ExpireAccessAsync(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await db.KeyAccessRevisions.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, x => x.Version + 1), ct);
        var now = clock.UtcNow;
        var expired = await db.DigitalPermissions.Include(x => x.Person)
            .Where(x => x.State != DigitalPermissionState.Revoked && x.State != DigitalPermissionState.RevocationPending &&
                (x.EndsAt <= now || !x.Person!.IsActive)).ToListAsync(ct);
        if (expired.Count == 0) { await tx.RollbackAsync(ct); return; }
        foreach (var p in expired)
        {
            var before = p.State; p.State = DigitalPermissionState.RevocationPending;
            audit.Record("keys.permission.auto-revoke-request", nameof(DigitalPermission), p.Id, null,
                details: new { p.PersonId, p.DeviceId, before, expired = p.EndsAt <= now });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task QueueRemindersAsync(CancellationToken ct)
    {
        var days = (await calendar.AllSitesAsync(ct)).Where(x => ReminderSchedule.IsDue(x, options.CurrentValue.ReminderTimeOfDay)).ToDictionary(x => x.SiteId, x => x.Today);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await db.KeyAccessRevisions.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, x => x.Version + 1), ct);
        var now = clock.UtcNow;
        var loans = await db.KeyLoans.Include(x => x.Person).Include(x => x.Copy).ThenInclude(x => x!.Definition).ThenInclude(x => x!.Building)
            .Where(x => x.ClosedAt == null && x.State != KeyLoanState.Reserved && x.DueDate != null).ToListAsync(ct);
        var queued = false;
        foreach (var loan in loans)
        {
            if (!days.TryGetValue(loan.Copy!.Definition!.Building!.SiteId, out var today) || today < loan.DueDate) continue;
            var key = $"key-access:{loan.PersonId}:reminder:{loan.Id}:{today:yyyy-MM-dd}";
            if (await db.Outbox.AnyAsync(x => x.DedupeKey == key, ct)) continue;
            queued = true;
            var email = loan.Person!.IsActive ? EmailAddresses.Normalize(loan.Person.Email) : null;
            db.Outbox.Add(new NotificationOutbox
            {
                Kind = NotificationKind.KeyAccess, DedupeKey = key, RecipientEmail = email,
                Status = email is null ? NotificationStatus.Skipped : NotificationStatus.Pending,
                SkipReason = email is null ? "Inactive person or invalid email" : null,
                Subject = "Key return reminder", Body = $"Key {loan.Copy.Tag} is due {loan.DueDate:yyyy-MM-dd}. Please arrange return with Facility.",
                CreatedAt = now, NextAttemptAt = now,
            });
        }
        if (!queued) { await tx.RollbackAsync(ct); return; }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
