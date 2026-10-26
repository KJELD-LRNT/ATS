namespace AssetTracker.Core.Domain;

/// <summary>Durable per-recipient mail outbox row, created atomically with the business change.</summary>
public class NotificationOutbox
{
    public long Id { get; set; }

    public NotificationKind Kind { get; set; }
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;

    /// <summary>Uniqueness key, e.g. "overdue:{loanId}:{yyyy-MM-dd}:{recipient}". Prevents duplicate queueing.</summary>
    public string DedupeKey { get; set; } = "";

    /// <summary>Null when the recipient was skipped (missing/invalid address).</summary>
    public string? RecipientEmail { get; set; }
    public string? SkipReason { get; set; }

    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";

    public int? LoanId { get; set; }

    /// <summary>
    /// Navigation so a row staged together with a new (unsaved) loan gets its <see cref="LoanId"/> on SaveChanges.
    /// </summary>
    public Loan? Loan { get; set; }

    /// <summary>The effective loan event (checkout/return/correction) this notice is about. Null for reminders.</summary>
    public long? LoanEventId { get; set; }

    /// <summary>
    /// Navigation so a row staged together with a new (unsaved) event gets its <see cref="LoanEventId"/> on SaveChanges.
    /// </summary>
    public LoanEvent? LoanEvent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int Attempts { get; set; }

    /// <summary>When the sender last tried this row (success or failure). Drives the "last error" ops view.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? LastError { get; set; }
}
