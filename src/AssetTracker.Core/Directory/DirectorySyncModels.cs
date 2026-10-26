using AssetTracker.Core.Domain;

namespace AssetTracker.Core.Directory;

/// <summary>One reported sync problem. Stored (as a JSON array) in <see cref="SyncRun.IssuesJson"/>.</summary>
/// <param name="Code">missing-id, missing-username, invalid-id, duplicate-id, duplicate-sid, sid-moved, unmapped-site, missing-site, low-count, fetch-failed.</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="DirectoryId">Affected directory identity, when known.</param>
/// <param name="Username">Affected username, when known.</param>
/// <param name="Count">Number of entries the issue covers (aggregated issues such as unmapped site values).</param>
public sealed record SyncIssue(string Code, string Message, string? DirectoryId = null, string? Username = null, int Count = 1);

/// <summary>Snapshot freshness as shown to IT and recorded on checkout.</summary>
/// <param name="LastSuccessAt">Finish time of the latest successful sync, or null if none ever succeeded.</param>
/// <param name="IsStale">True when there is no successful sync or it is older than <see cref="DirectorySyncOptions.StaleAfter"/>.</param>
/// <param name="LastRunStatus">Status of the most recent run (any outcome), if any.</param>
/// <param name="LastRunAt">Start time of the most recent run, if any.</param>
/// <param name="LastRunError">Error or rejection reason of the most recent run when it did not succeed.</param>
public sealed record DirectorySyncStatus(
    DateTimeOffset? LastSuccessAt,
    bool IsStale,
    SyncRunStatus? LastRunStatus,
    DateTimeOffset? LastRunAt,
    string? LastRunError)
{
    /// <summary>The most recent run failed or was rejected (the previous good snapshot is still in use).</summary>
    public bool LastRunUnsuccessful => LastRunStatus is SyncRunStatus.Failed or SyncRunStatus.Rejected;
}
