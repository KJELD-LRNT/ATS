namespace AssetTracker.Core.Domain;

/// <summary>
/// An assignment of a tagged asset (quantity 1) or a pool quantity to a person.
/// Columns hold the <b>effective</b> (corrected) state; the full append-only trail lives in <see cref="LoanEvent"/>.
/// </summary>
public class Loan
{
    public int Id { get; set; }

    /// <summary>Exactly one of AssetId / PoolId is set.</summary>
    public int? AssetId { get; set; }
    public IndividualAsset? Asset { get; set; }

    public int? PoolId { get; set; }
    public Pool? Pool { get; set; }

    public int HolderId { get; set; }
    public Person? Holder { get; set; }

    /// <summary>Quantity checked out (1 for tagged assets).</summary>
    public int Quantity { get; set; }

    /// <summary>Quantity returned so far. Open while ReturnedQuantity &lt; Quantity.</summary>
    public int ReturnedQuantity { get; set; }

    public int OutstandingQuantity => Quantity - ReturnedQuantity;

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Optional due date as a calendar day in the loan site's timezone (see <c>SiteCalendar</c>).</summary>
    public DateOnly? DueDate { get; set; }

    /// <summary>Set when fully returned. Null while open (drives the one-open-loan-per-asset unique index).</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    public string? CheckoutCondition { get; set; }
    public string? CheckoutNote { get; set; }
    public string? ReturnCondition { get; set; }
    public string? ReturnNote { get; set; }

    /// <summary>Holder details snapshotted at checkout.</summary>
    public string HolderNameSnapshot { get; set; } = "";
    public string? HolderEmailSnapshot { get; set; }

    public string CreatedBy { get; set; } = "";

    /// <summary>Incremented on every event (return, correction).</summary>
    public int EffectiveVersion { get; set; } = 1;

    public bool IsOpen => ClosedAt is null;

    public List<LoanEvent> Events { get; set; } = [];
}
