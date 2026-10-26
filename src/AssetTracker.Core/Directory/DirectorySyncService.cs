using System.Text.Json;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetTracker.Core.Directory;

/// <summary>
/// Full people sync from the directory into the local <see cref="Person"/> snapshot.
/// <para>
/// A run either applies a complete, validated snapshot or changes nothing: a failed fetch ends as
/// <see cref="SyncRunStatus.Failed"/>, a result that fails the safety checks ends as
/// <see cref="SyncRunStatus.Rejected"/>, and in both cases the last good snapshot stays in place.
/// People are keyed on the stable directory id (rename never creates a second person) and are never deleted:
/// people absent from a successful full result, or disabled in AD, become inactive.
/// </para>
/// <para>
/// Entries without id/username, or sharing an id or SID with another entry, are quarantined (skipped and reported;
/// an existing person with a quarantined id is left unchanged). An entry whose site value maps to no
/// <see cref="Site.DirectoryValue"/> is still synced with no site and reported as an unmapped-site issue.
/// </para>
/// <para>
/// Sponsor decision D6: a run that ends <see cref="SyncRunStatus.Failed"/> or <see cref="SyncRunStatus.Rejected"/>
/// stages one <see cref="NotificationKind.SystemAlert"/> mail to the IT mailbox (dedupe key
/// <c>sync-alert:{runId}</c>) in the same save as the run's completion.
/// </para>
/// Not authorization-guarded: it is run by the worker (system actor) and by <c>AdminService.RunSyncNowAsync</c>,
/// which enforces the admin role.
/// </summary>
public class DirectorySyncService(
    AppDbContext db,
    IDirectorySource source,
    IClock clock,
    AuditWriter audit,
    NotificationService notifications,
    IOptionsMonitor<DirectorySyncOptions> options,
    ILogger<DirectorySyncService> logger)
{
    /// <summary>Individual issues kept per run; the remainder is summarized.</summary>
    public const int MaxIssues = 200;

    private const int MaxDirectoryIdLength = 64;
    private const int MaxErrorLength = 4000;

    /// <summary>Dedupe key of the D6 alert for a failed or rejected run.</summary>
    public static string AlertDedupeKey(int runId) => $"sync-alert:{runId}";

    /// <summary>Issues listed in the alert mail; the full list is on the sync history page.</summary>
    private const int MaxAlertIssues = 10;

    public async Task<SyncRun> RunAsync(string triggeredBy, CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        var startedAt = clock.UtcNow;
        var runningCutoff = startedAt - o.RunningTimeout;
        var running = await db.SyncRuns.Where(r => r.Status == SyncRunStatus.Running)
            .Select(r => r.StartedAt).ToListAsync(ct);
        if (running.Any(s => s > runningCutoff))
            throw new ConflictException("sync.running", "A directory sync is already running.");

        var run = new SyncRun { StartedAt = startedAt, Status = SyncRunStatus.Running, TriggeredBy = triggeredBy };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);

        IReadOnlyList<DirectoryEntry> entries;
        try
        {
            entries = await source.FetchAllUsersAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Directory fetch failed for sync run {RunId}; the previous snapshot is kept.", run.Id);
            await FailAsync(run, ex, "fetch-failed");
            if (ex is OperationCanceledException) throw;
            return run;
        }

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await ApplyAsync(run, entries, o, ct);
            await tx.CommitAsync(ct);
            logger.LogInformation(
                "Directory sync run {RunId} {Status}: fetched {Fetched}, mapped {Mapped}, quarantined {Quarantined}, created {Created}, updated {Updated}, deactivated {Deactivated}.",
                run.Id, run.Status, run.Fetched, run.Mapped, run.Quarantined, run.Created, run.Updated, run.Deactivated);
            return run;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Applying directory sync run {RunId} failed; the previous snapshot is kept.", run.Id);
            db.ChangeTracker.Clear();
            run.Created = run.Updated = run.Deactivated = 0;
            db.Entry(run).State = EntityState.Modified;
            await FailAsync(run, ex, "apply-failed");
            if (ex is OperationCanceledException) throw;
            return run;
        }
    }

    /// <summary>Freshness of the people snapshot (for the IT banner and checkout audit).</summary>
    public async Task<DirectorySyncStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var lastSuccess = await db.SyncRuns.Where(r => r.Status == SyncRunStatus.Succeeded)
            .OrderByDescending(r => r.Id).Select(r => r.FinishedAt).FirstOrDefaultAsync(ct);
        var last = await db.SyncRuns.OrderByDescending(r => r.Id)
            .Select(r => new { r.Status, r.StartedAt, r.Error }).FirstOrDefaultAsync(ct);

        var stale = lastSuccess is null || clock.UtcNow - lastSuccess.Value > options.CurrentValue.StaleAfter;
        return new DirectorySyncStatus(lastSuccess, stale, last?.Status, last?.StartedAt,
            last is { Status: SyncRunStatus.Failed or SyncRunStatus.Rejected } ? last.Error : null);
    }

    private sealed record Candidate(
        string DirectoryId, string? Sid, string Username, string DisplayName, string? JobTitle, string? Email,
        string? SiteValue, bool Enabled);

    private sealed record Classification(
        List<SyncIssue> Issues, Dictionary<string, Candidate> Valid, HashSet<string> QuarantinedIds,
        Dictionary<string, int> SiteByValue);

    /// <summary>
    /// Read-only dry run for the settings page: fetches from the directory with the current settings and applies the
    /// same validation and safety checks as <see cref="RunAsync"/>, but writes nothing (no sync run, no people changes).
    /// Fetch errors propagate. Not authorization-guarded (see <c>SettingsService.PreviewDirectoryAsync</c>).
    /// </summary>
    /// <param name="sampleSize">Maximum number of sample entries returned.</param>
    public async Task<DirectoryPreview> PreviewAsync(int sampleSize = 5, CancellationToken ct = default)
    {
        var entries = await source.FetchAllUsersAsync(ct);
        var c = await ClassifyAsync(entries, ct);
        var people = await db.People.AsNoTracking().ToListAsync(ct);
        var rejection = SafetyRejection(c, people, options.CurrentValue, out var currentActive, out var wouldDeactivate);
        var valid = c.Valid.Values.ToList();
        return new DirectoryPreview(
            Fetched: entries.Count,
            Valid: valid.Count,
            Quarantined: entries.Count - valid.Count,
            MappedToSite: valid.Count(v => v.SiteValue is not null && c.SiteByValue.ContainsKey(v.SiteValue)),
            UnmappedSite: valid.Count(v => v.SiteValue is not null && !c.SiteByValue.ContainsKey(v.SiteValue)),
            MissingSite: valid.Count(v => v.SiteValue is null),
            Disabled: valid.Count(v => !v.Enabled),
            CurrentActive: currentActive,
            WouldDeactivate: wouldDeactivate,
            WouldReject: rejection,
            Issues: c.Issues.Take(MaxIssues).ToList(),
            Sample: valid.Take(Math.Max(0, sampleSize))
                .Select(v => new DirectoryPreviewSample(v.Username, v.DisplayName, v.SiteValue,
                    v.SiteValue is not null && c.SiteByValue.ContainsKey(v.SiteValue), v.Enabled))
                .ToList());
    }

    /// <summary>Steps 1–3: per-entry validation, duplicate quarantine and site mapping (no changes).</summary>
    private async Task<Classification> ClassifyAsync(IReadOnlyList<DirectoryEntry> entries, CancellationToken ct)
    {
        var issues = new List<SyncIssue>();

        // 1. Per-entry validation.
        var candidates = new List<Candidate>(entries.Count);
        foreach (var e in entries)
        {
            var id = Clean(e.DirectoryId);
            var username = Clean(e.Username);
            if (id is null)
                issues.Add(new SyncIssue("missing-id", "Entry has no directory id; skipped.", Username: username));
            else if (id.Length > MaxDirectoryIdLength)
                issues.Add(new SyncIssue("invalid-id", $"Directory id longer than {MaxDirectoryIdLength} characters; skipped.", Username: username));
            else if (username is null)
                issues.Add(new SyncIssue("missing-username", "Entry has no username; skipped.", DirectoryId: id));
            else
                candidates.Add(new Candidate(id, Clean(e.Sid), username, Clean(e.DisplayName) ?? username,
                    Clean(e.JobTitle), Clean(e.Email), Clean(e.SiteValue), e.Enabled));
        }

        // 2. Duplicates: quarantine every entry that shares an id (or a SID) with another entry.
        var quarantinedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in candidates.GroupBy(c => c.DirectoryId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            quarantinedIds.Add(g.Key);
            issues.Add(new SyncIssue("duplicate-id", $"{g.Count()} entries share this directory id; all skipped.",
                DirectoryId: g.Key, Count: g.Count()));
        }
        foreach (var g in candidates.Where(c => c.Sid is not null && !quarantinedIds.Contains(c.DirectoryId))
                     .GroupBy(c => c.Sid!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            foreach (var c in g)
            {
                quarantinedIds.Add(c.DirectoryId);
                issues.Add(new SyncIssue("duplicate-sid", "Entry shares its SID with another entry; skipped.",
                    DirectoryId: c.DirectoryId, Username: c.Username));
            }
        }
        var valid = candidates.Where(c => !quarantinedIds.Contains(c.DirectoryId))
            .ToDictionary(c => c.DirectoryId, StringComparer.OrdinalIgnoreCase);

        // 3. Site mapping (unmapped people are still synced, without a site, and reported).
        var siteByValue = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in await db.Sites.AsNoTracking().Where(s => s.DirectoryValue != null).ToListAsync(ct))
            siteByValue.TryAdd(s.DirectoryValue!.Trim(), s.Id);
        foreach (var g in valid.Values.Where(c => c.SiteValue is not null && !siteByValue.ContainsKey(c.SiteValue))
                     .GroupBy(c => c.SiteValue!, StringComparer.OrdinalIgnoreCase))
            issues.Add(new SyncIssue("unmapped-site",
                $"Site value '{g.Key}' is not mapped to any site; {g.Count()} people synced without a site.", Count: g.Count()));
        var missingSite = valid.Values.Count(c => c.SiteValue is null);
        if (missingSite > 0)
            issues.Add(new SyncIssue("missing-site", $"{missingSite} people have no site value; synced without a site.",
                Count: missingSite));

        return new Classification(issues, valid, quarantinedIds, siteByValue);
    }

    /// <summary>Step 4: safety checks against the current snapshot; returns the rejection reason or null.</summary>
    private static string? SafetyRejection(Classification c, List<Person> people, DirectorySyncOptions o,
        out int currentActive, out int wouldDeactivate)
    {
        var valid = c.Valid;
        currentActive = people.Count(p => p.IsActive);
        wouldDeactivate = people.Count(p => p.IsActive && !c.QuarantinedIds.Contains(p.DirectoryId) &&
                                            (!valid.TryGetValue(p.DirectoryId, out var v) || !v.Enabled));
        var active = currentActive;
        var drop = wouldDeactivate;
        return valid.Count == 0 ? "The directory returned no valid entries."
            : o.MinimumExpectedCount > 0 && valid.Count < o.MinimumExpectedCount
                ? $"Only {valid.Count} valid entries; at least {o.MinimumExpectedCount} expected."
            : o.MaxDropFraction is { } f && active > 0 && drop > active * f
                ? $"Run would deactivate {drop} of {active} active people (limit {f:P0})."
            : null;
    }

    private async Task ApplyAsync(SyncRun run, IReadOnlyList<DirectoryEntry> entries, DirectorySyncOptions o,
        CancellationToken ct)
    {
        run.Fetched = entries.Count;
        var classification = await ClassifyAsync(entries, ct);
        var (issues, valid, quarantinedIds, siteByValue) = classification;
        run.Mapped = valid.Count;
        run.Quarantined = run.Fetched - run.Mapped;

        // 4. Safety checks against the current snapshot.
        var people = await db.People.ToListAsync(ct);
        var byId = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in people) byId.TryAdd(p.DirectoryId, p);

        var rejection = SafetyRejection(classification, people, o, out _, out _);
        if (rejection is not null)
        {
            issues.Add(new SyncIssue("low-count", rejection + " Snapshot not applied."));
            await FinishAsync(run, SyncRunStatus.Rejected, issues, rejection, ct);
            return;
        }

        // 5. Apply. First release SIDs that moved to another directory id, so the unique SID index never clashes.
        var now = clock.UtcNow;
        var bySid = people.Where(p => p.Sid is not null)
            .GroupBy(p => p.Sid!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var released = false;
        foreach (var c in valid.Values.Where(c => c.Sid is not null))
        {
            if (!bySid.TryGetValue(c.Sid!, out var holders)) continue;
            foreach (var other in holders.Where(h => !string.Equals(h.DirectoryId, c.DirectoryId, StringComparison.OrdinalIgnoreCase)))
            {
                other.Sid = null;
                released = true;
                issues.Add(new SyncIssue("sid-moved", "SID now belongs to another directory id; cleared on the previous person.",
                    DirectoryId: other.DirectoryId, Username: other.Username));
            }
        }
        if (released) await db.SaveChangesAsync(ct);

        foreach (var c in valid.Values)
        {
            int? siteId = c.SiteValue is not null && siteByValue.TryGetValue(c.SiteValue, out var sid) ? sid : null;
            if (!byId.TryGetValue(c.DirectoryId, out var p))
            {
                p = new Person { DirectoryId = c.DirectoryId, FirstSeenAt = now };
                Update(p, c, siteId);
                db.People.Add(p);
                run.Created++;
            }
            else if (p.IsAnonymized)
            {
                // Anonymized under client direction: never refresh or reactivate (docs/privacy-and-retention.md).
                if (c.Enabled)
                    issues.Add(new SyncIssue("anonymized-present",
                        "An anonymized person is enabled in the directory; left anonymized and inactive.",
                        DirectoryId: c.DirectoryId));
                continue;
            }
            else
            {
                var wasActive = p.IsActive;
                var changed = Update(p, c, siteId);
                if (wasActive && !p.IsActive) run.Deactivated++;
                else if (changed) run.Updated++;
            }
            p.LastSyncedAt = now;
        }

        foreach (var p in people.Where(p => p.IsActive && !valid.ContainsKey(p.DirectoryId) && !quarantinedIds.Contains(p.DirectoryId)))
        {
            p.IsActive = false;
            run.Deactivated++;
        }

        await FinishAsync(run, SyncRunStatus.Succeeded, issues, null, ct);
    }

    /// <summary>Copies directory fields onto the person; returns true if anything changed.</summary>
    private static bool Update(Person p, Candidate c, int? siteId)
    {
        var changed = false;
        void Set<T>(T current, T value, Action<T> assign)
        {
            if (EqualityComparer<T>.Default.Equals(current, value)) return;
            assign(value);
            changed = true;
        }

        Set(p.Username, Truncate(c.Username, 256)!, v => p.Username = v);
        Set(p.DisplayName, Truncate(c.DisplayName, 256)!, v => p.DisplayName = v);
        Set(p.JobTitle, Truncate(c.JobTitle, 256), v => p.JobTitle = v);
        Set(p.Email, Truncate(c.Email, 320), v => p.Email = v);
        Set(p.Sid, Truncate(c.Sid, 100), v => p.Sid = v);
        Set(p.SiteId, siteId, v => p.SiteId = v);
        Set(p.IsActive, c.Enabled, v => p.IsActive = v);
        return changed;
    }

    private async Task FailAsync(SyncRun run, Exception ex, string code)
    {
        var error = Truncate($"{ex.GetType().Name}: {ex.Message}", MaxErrorLength);
        await FinishAsync(run, SyncRunStatus.Failed, [new SyncIssue(code, error!)], error, CancellationToken.None);
    }

    private async Task FinishAsync(SyncRun run, SyncRunStatus status, List<SyncIssue> issues, string? error,
        CancellationToken ct)
    {
        if (issues.Count > MaxIssues)
        {
            var dropped = issues.Count - MaxIssues;
            issues = [.. issues.Take(MaxIssues), new SyncIssue("truncated", $"{dropped} further issues not listed.", Count: dropped)];
        }
        run.Status = status;
        run.FinishedAt = clock.UtcNow;
        run.Error = Truncate(error, MaxErrorLength);
        run.IssuesJson = issues.Count == 0 ? null : JsonSerializer.Serialize(issues);
        audit.Record("directory.sync", nameof(SyncRun), run.Id, null, reason: run.Error, details: new
        {
            run.Status, run.TriggeredBy, run.Fetched, run.Mapped, run.Quarantined, run.Created, run.Updated,
            run.Deactivated, Issues = issues.Count,
        });
        if (status is SyncRunStatus.Failed or SyncRunStatus.Rejected)
        {
            var (subject, body) = AlertMail(run, issues);
            await notifications.QueueSystemAlertAsync(subject, body, AlertDedupeKey(run.Id), ct);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// D6 alert text: run id, status, counts and a short, sanitized summary (first line of each message only, so no
    /// stack traces; truncated). Issue usernames/ids are left out: the history page has the details.
    /// </summary>
    private static (string Subject, string Body) AlertMail(SyncRun run, List<SyncIssue> issues)
    {
        var verb = run.Status == SyncRunStatus.Failed ? "failed" : "rejected";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Hello IT,").AppendLine();
        sb.AppendLine(run.Status == SyncRunStatus.Failed
            ? $"Directory sync run {run.Id} failed. The previous people snapshot is still in use."
            : $"Directory sync run {run.Id} was rejected by the safety checks and not applied. The previous people snapshot is still in use.");
        sb.AppendLine();
        sb.AppendLine($"Run:                {run.Id}");
        sb.AppendLine($"Status:             {run.Status}");
        sb.AppendLine($"Triggered by:       {run.TriggeredBy}");
        sb.AppendLine($"Started (UTC):      {run.StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Fetched:            {run.Fetched}");
        sb.AppendLine($"Mapped:             {run.Mapped}");
        sb.AppendLine($"Quarantined:        {run.Quarantined}");
        if (run.Error is not null) sb.AppendLine($"Error:              {Sanitize(run.Error)}");
        if (issues.Count > 0)
        {
            sb.AppendLine().AppendLine($"Issues ({issues.Count}):");
            foreach (var i in issues.Take(MaxAlertIssues)) sb.AppendLine($"- {i.Code}: {Sanitize(i.Message)}");
            if (issues.Count > MaxAlertIssues) sb.AppendLine($"- ... {issues.Count - MaxAlertIssues} more on the sync history page.");
        }
        sb.AppendLine().AppendLine("See Directory sync in the asset tracker for the full run history.");
        sb.AppendLine().AppendLine("This is an automated message from the asset tracker.");
        return ($"Asset Tracker: AD sync {verb} (run {run.Id})", sb.ToString());
    }

    private static string Sanitize(string text)
    {
        var line = text.ReplaceLineEndings("\n").Split("\n", 2)[0].Trim();
        return line.Length <= 300 ? line : line[..300] + "...";
    }

    /// <summary>Parses <see cref="SyncRun.IssuesJson"/> for display.</summary>
    public static IReadOnlyList<SyncIssue> ParseIssues(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<SyncIssue>>(json) ?? [];

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
