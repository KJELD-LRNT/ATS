namespace AssetTracker.Core.Domain;

/// <summary>
/// Append-only loan history. Corrections are <see cref="LoanEventType.Corrected"/> events that
/// supersede an earlier event (via <see cref="SupersedesEventId"/>); nothing is ever deleted.
/// </summary>
public class LoanEvent
{
    public long Id { get; set; }

    public int LoanId { get; set; }
    public Loan? Loan { get; set; }

    public LoanEventType Type { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string Actor { get; set; } = "";

    /// <summary>Holder at the time of the event (for corrections: the corrected holder).</summary>
    public int HolderId { get; set; }

    public int Quantity { get; set; }
    public DateOnly? DueDate { get; set; }
    public string? Condition { get; set; }
    public string? Note { get; set; }

    /// <summary>Required for corrections.</summary>
    public string? Reason { get; set; }

    public long? SupersedesEventId { get; set; }
    public LoanEvent? Supersedes { get; set; }

    /// <summary>JSON describing the field-level change for corrections.</summary>
    public string? ChangeJson { get; set; }

    /// <summary>Set when the directory snapshot was stale at the time of the event.</summary>
    public bool DirectoryStale { get; set; }
}
