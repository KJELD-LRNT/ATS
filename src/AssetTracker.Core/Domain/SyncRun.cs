namespace AssetTracker.Core.Domain;

public class SyncRun
{
    public int Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public SyncRunStatus Status { get; set; }
    public string TriggeredBy { get; set; } = "";

    public int Fetched { get; set; }
    public int Mapped { get; set; }
    public int Quarantined { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Deactivated { get; set; }

    /// <summary>Human-readable issues (unmapped site, missing id, duplicates...), JSON array.</summary>
    public string? IssuesJson { get; set; }
    public string? Error { get; set; }
}
