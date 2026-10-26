namespace AssetTracker.Core.Directory;

/// <summary>
/// Settings for the scheduled AD people sync (config section <c>DirectorySync</c>).
/// Production values (interval, thresholds) come from Stage 0; only the safety net is defaulted ON.
/// </summary>
public class DirectorySyncOptions
{
    public const string SectionName = "DirectorySync";

    /// <summary>
    /// How often the worker runs a full sync. No default: the worker refuses to schedule sync until this is set.
    /// </summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>
    /// Absolute floor: a run whose valid (non-quarantined) entry count is below this is rejected.
    /// 0 disables this check (set it from the client's expected directory size in Stage 0).
    /// A run with zero valid entries is always rejected regardless of this setting.
    /// </summary>
    public int MinimumExpectedCount { get; set; }

    /// <summary>
    /// Relative safety check, ON by default: a run is rejected when it would deactivate more than this fraction
    /// of the currently active people (absent from the result or disabled in AD). Default 0.2 (20%).
    /// Set to null to disable (not recommended).
    /// </summary>
    public double? MaxDropFraction { get; set; } = 0.2;

    /// <summary>
    /// The snapshot is reported as stale to IT when the last successful sync is older than this.
    /// Default 24h is a placeholder until the sync frequency is agreed in Stage 0.
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// A run still marked Running after this long is assumed abandoned (e.g. process crash) and no longer blocks a new run.
    /// </summary>
    public TimeSpan RunningTimeout { get; set; } = TimeSpan.FromHours(1);
}
