namespace AssetTracker.Core.Domain;

/// <summary>Append-only audit record. No update/delete path exists in the application.</summary>
public class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Actor { get; set; } = "";

    /// <summary>E.g. "asset.create", "loan.checkout", "export.inventory".</summary>
    public string Action { get; set; } = "";

    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public int? SiteId { get; set; }
    public string? Reason { get; set; }
    public string? DetailsJson { get; set; }
    public string CorrelationId { get; set; } = "";
}
