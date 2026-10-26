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
    public long? LoanEventId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? LastError { get; set; }
}
