using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;

namespace AssetTracker.Core.Reporting;

/// <summary>
/// Server-side report filters. All are optional and are applied <b>on top of</b> <c>AccessScope</c>, so a
/// manipulated <see cref="SiteId"/> can only narrow what the principal may already read.
/// <list type="bullet">
/// <item><see cref="Query"/>: inventory — tag, SKU, description, serial; loan reports — holder name, username,
/// email, checkout name snapshot, asset tag or pool SKU.</item>
/// <item><see cref="From"/>/<see cref="To"/> (inclusive): assignments and overdue — due date (site-local
/// calendar day); history — loan start date (UTC calendar day). Ignored by the inventory report.</item>
/// <item><see cref="State"/>: inventory only (tagged asset state).</item>
/// </list>
/// </summary>
public sealed record ReportFilter(
    string? Query = null, int? SiteId = null, int? TypeId = null, AssetState? State = null,
    DateOnly? From = null, DateOnly? To = null,
    int Page = 1, int PageSize = Paging.DefaultPageSize)
{
    /// <summary>Filters only (no paging), for audit details.</summary>
    public object ForAudit() => new { Query, SiteId, TypeId, State = State?.ToString(), From, To };
}

/// <summary>A tagged asset in the inventory report, with its current holder when checked out.</summary>
public sealed record InventoryAssetRow(
    int Id, string Tag, string Type, int SiteId, string SiteKey, AssetState State, string? Vendor, string? Model,
    string Description, string? SerialNumber, string? HolderName, string? HolderUsername, DateOnly? DueDate)
{
    /// <summary>Phone number fields of the asset, "; "-separated (filled in after the query, see <c>ReportService</c>).</summary>
    public string? PhoneNumbers { get; init; }

    /// <summary>IMEIs of the asset, "; "-separated (a dual-SIM phone has two).</summary>
    public string? Imeis { get; init; }
}

/// <summary>A quantity pool balance. OnHand = Available + Outstanding.</summary>
public sealed record PoolBalanceRow(
    int Id, string Sku, string Type, int SiteId, string SiteKey, string Description, int Available, int Outstanding,
    int? MinimumStock = null)
{
    public int OnHand => Available + Outstanding;
    public bool IsBelowMinimum => MinimumStock is { } m && Available < m;
}

public sealed record AssetCount(string SiteKey, string Type, AssetState State, int Count);

public sealed record PoolTotal(string SiteKey, string Type, int Pools, int Available, int Outstanding)
{
    public int OnHand => Available + Outstanding;
}

/// <summary>
/// Counts for the reports landing page; already scoped to the principal's sites. <see cref="OverdueLoans"/> uses
/// each loan's site-local day (<c>SiteCalendar</c>).
/// </summary>
public sealed record InventorySummary(
    IReadOnlyList<AssetCount> Assets, IReadOnlyList<PoolTotal> Pools,
    int OpenLoans, int OpenPoolUnits, int OverdueLoans);

/// <summary>
/// One loan (assignment) in the effective, corrected state, with the holder's full synced snapshot.
/// Tagged loans report <see cref="AssetTag"/> and quantity 1; pool loans report <see cref="PoolSku"/> and quantities.
/// </summary>
public sealed record LoanRow
{
    public int LoanId { get; init; }
    public bool IsPool { get; init; }
    public string? AssetTag { get; init; }
    public string? PoolSku { get; init; }
    public string ItemDescription { get; init; } = "";
    public string Type { get; init; } = "";

    /// <summary>The loan's site: the asset's current site, or the pool's site.</summary>
    public int SiteId { get; init; }
    public string SiteKey { get; init; } = "";

    public int Quantity { get; init; }
    public int ReturnedQuantity { get; init; }
    public int OutstandingQuantity => Quantity - ReturnedQuantity;

    public DateTimeOffset StartedAt { get; init; }
    public DateOnly? DueDate { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public bool IsOpen => ClosedAt is null;

    public int HolderId { get; init; }
    public string HolderDisplayName { get; init; } = "";
    public string HolderUsername { get; init; } = "";
    public string? HolderEmail { get; init; }
    public string? HolderJobTitle { get; init; }
    public string? HolderSiteKey { get; init; }
    public bool HolderActive { get; init; }

    /// <summary>Holder name as recorded at checkout (may differ after an AD rename).</summary>
    public string HolderNameSnapshot { get; init; } = "";

    public string? CheckoutCondition { get; init; }
    public string? ReturnCondition { get; init; }
    public string CreatedBy { get; init; } = "";
    public int EffectiveVersion { get; init; }

    public int CorrectionCount { get; init; }
    public string? LatestCorrectionReason { get; init; }

    /// <summary>
    /// Set by the overdue report: whole days past due at the loan's site, 0 = due today
    /// (see <c>OverduePolicy.DaysOverdue</c>).
    /// </summary>
    public int? DaysOverdue { get; init; }

    /// <summary>Set by the due-soon list: whole days until the due date at the loan's site (1 = tomorrow).</summary>
    public int? DaysUntilDue { get; init; }

    public string ItemLabel => AssetTag ?? PoolSku ?? "";
}

/// <summary>One append-only loan event (including corrections) for the event-level history export.</summary>
public sealed record LoanEventRow
{
    public long EventId { get; init; }
    public int LoanId { get; init; }
    public LoanEventType Type { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public string Actor { get; init; } = "";
    public string? AssetTag { get; init; }
    public string? PoolSku { get; init; }
    public string SiteKey { get; init; } = "";
    public int HolderId { get; init; }
    public string? HolderDisplayName { get; init; }
    public string? HolderUsername { get; init; }
    public string? HolderEmail { get; init; }
    public int Quantity { get; init; }
    public DateOnly? DueDate { get; init; }
    public string? Condition { get; init; }
    public string? Note { get; init; }
    public string? Reason { get; init; }
    public long? SupersedesEventId { get; init; }

    /// <summary>The latest correction event that supersedes this one, if any.</summary>
    public long? SupersededByEventId { get; init; }
}

/// <summary>A generated CSV export.</summary>
public sealed record CsvExport(string FileName, byte[] Content, int RowCount)
{
    public const string ContentType = "text/csv; charset=utf-8";
}
