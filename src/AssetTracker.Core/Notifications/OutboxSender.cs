using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetTracker.Core.Notifications;

public sealed record OutboxBatchResult(int Claimed, int Sent, int Retrying, int Failed);

/// <summary>
/// Delivers due <see cref="NotificationStatus.Pending"/> outbox rows through <see cref="IMailRelay"/>.
/// <para>
/// Claiming: a row is claimed with a conditional update that moves <c>NextAttemptAt</c> to now + lease only if it
/// still holds the value just read; zero rows affected means another worker won. The outcome is written with the
/// same condition (still holding our lease), so a slow sender whose lease expired cannot overwrite a newer claim.
/// A worker that dies mid-send leaves the row Pending; it becomes due again when the lease expires.
/// </para>
/// <para>
/// Delivery is at-least-once, not exactly-once: a crash or timeout after the relay accepted a message, but before
/// the row is marked Sent, sends it again.
/// </para>
/// Requires no role (runs in the worker as the system user).
/// </summary>
public class OutboxSender(AppDbContext db, IMailRelay relay, IClock clock, IOptions<NotificationOptions> options,
    ILogger<OutboxSender> logger)
{
    public const int MaxErrorLength = 2000;

    /// <summary>Claims and sends up to <paramref name="batchSize"/> due rows.</summary>
    public async Task<OutboxBatchResult> ProcessDueAsync(int batchSize, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.UtcNow;
        var due = await db.Outbox.AsNoTracking()
            .Where(x => x.Status == NotificationStatus.Pending && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.NextAttemptAt })
            .Take(batchSize)
            .ToListAsync(ct);

        int claimed = 0, sent = 0, retrying = 0, failed = 0;
        foreach (var candidate in due)
        {
            ct.ThrowIfCancellationRequested();
            var lease = clock.UtcNow.Add(o.ClaimLease);
            var won = await db.Outbox
                .Where(x => x.Id == candidate.Id && x.Status == NotificationStatus.Pending
                            && x.NextAttemptAt == candidate.NextAttemptAt)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, lease), ct);
            if (won == 0) continue;
            claimed++;

            var row = await db.Outbox.AsNoTracking().FirstAsync(x => x.Id == candidate.Id, ct);
            if (row.RecipientEmail is null)
            {
                // Should not happen (skipped rows are never Pending); fail it visibly rather than loop.
                await FinishAsync(row.Id, lease, NotificationStatus.Failed, row.Attempts, "No recipient address.", null, ct);
                failed++;
                continue;
            }

            try
            {
                await relay.SendAsync(new MailMessage(row.RecipientEmail, row.Subject, row.Body), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Shutting down: the lease expires and the row is retried; not counted as an attempt.
            }
            catch (Exception ex)
            {
                var attempts = row.Attempts + 1;
                var exhausted = attempts >= o.MaxAttempts;
                var error = Truncate($"{ex.GetType().Name}: {ex.Message}");
                await FinishAsync(row.Id, lease, exhausted ? NotificationStatus.Failed : NotificationStatus.Pending,
                    attempts, error, exhausted ? null : clock.UtcNow.Add(Backoff(attempts, o)), ct);
                if (exhausted)
                {
                    failed++;
                    logger.LogError(ex, "Outbox row {Id} failed permanently after {Attempts} attempts", row.Id, attempts);
                }
                else
                {
                    retrying++;
                    logger.LogWarning(ex, "Outbox row {Id} send attempt {Attempts} failed; will retry", row.Id, attempts);
                }
                continue;
            }

            // Use CancellationToken.None: the mail is already out; recording that must not be skipped by shutdown.
            await FinishAsync(row.Id, lease, NotificationStatus.Sent, row.Attempts + 1, null, null, CancellationToken.None);
            sent++;
        }

        return new OutboxBatchResult(claimed, sent, retrying, failed);
    }

    /// <summary>Exponential backoff after the n-th failed attempt: initial × 2^(n-1), capped.</summary>
    public static TimeSpan Backoff(int attempts, NotificationOptions o)
    {
        var factor = Math.Pow(2, Math.Clamp(attempts - 1, 0, 30));
        var ticks = Math.Min(o.InitialRetryDelay.Ticks * factor, o.MaxRetryDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private async Task FinishAsync(long id, DateTimeOffset lease, NotificationStatus status, int attempts,
        string? error, DateTimeOffset? nextAttemptAt, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var next = nextAttemptAt ?? now;
        var sentAt = status == NotificationStatus.Sent ? now : (DateTimeOffset?)null;
        var updated = await db.Outbox
            .Where(x => x.Id == id && x.Status == NotificationStatus.Pending && x.NextAttemptAt == lease)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.Attempts, attempts)
                .SetProperty(x => x.LastAttemptAt, now)
                .SetProperty(x => x.NextAttemptAt, next)
                .SetProperty(x => x.SentAt, sentAt)
                .SetProperty(x => x.LastError, x => error ?? x.LastError), ct);
        if (updated == 0)
            logger.LogWarning("Outbox row {Id}: lease lost before the outcome ({Status}) could be recorded", id, status);
    }

    private static string Truncate(string s) => s.Length <= MaxErrorLength ? s : s[..MaxErrorLength];
}
