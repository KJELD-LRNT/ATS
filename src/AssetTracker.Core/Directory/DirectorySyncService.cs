using System.Text.Json;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
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
/// Not authorization-guarded: it is run by the worker (system actor) and by <c>AdminService.RunSyncNowAsync</c>,
/// which enforces the admin role.
/// </summary>
public class DirectorySyncService(
    AppDbContext db,
    IDirectorySource source,
    IClock clock,
    AuditWriter audit,
    IOptions<DirectorySyncOptions> options,
    ILogger<DirectorySyncService> logger)
{
    /// <summary>Individual issues kept per run; the remainder is summarized.</summary>
    public const int MaxIssues = 200;

    private const int MaxDirectoryIdLength = 64;
    private const int MaxErrorLength = 4000;

    public async Task<SyncRun> RunAsync(string triggeredBy, CancellationToken ct = default)
    {
        var o = options.Value;
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

        var stale = lastSuccess is null || clock.UtcNow - lastSuccess.Value > options.Value.StaleAfter;
        return new DirectorySyncStatus(lastSuccess, stale, last?.Status, last?.StartedAt,
            last is { Status: SyncRunStatus.Failed or SyncRunStatus.Rejected } ? last.Error : null);
    }

    private sealed record Candidate(
        string DirectoryId, string? Sid, string Username, string DisplayName, string? JobTitle, string? Email,
        string? SiteValue, bool Enabled);

    private async Task ApplyAsync(SyncRun run, IReadOnlyList<DirectoryEntry> entries, DirectorySyncOptions o,
        CancellationToken ct)
    {
        var issues = new List<SyncIssue>();
        run.Fetched = entries.Count;

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
        run.Mapped = valid.Count;
        run.Quarantined = run.Fetched - run.Mapped;

        // 3. Site mapping (unmapped people are still synced, without a site, and reported).
        var siteByValue = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in await db.Sites.Where(s => s.DirectoryValue != null).ToListAsync(ct))
            siteByValue.TryAdd(s.DirectoryValue!.Trim(), s.Id);
        foreach (var g in valid.Values.Where(c => c.SiteValue is not null && !siteByValue.ContainsKey(c.SiteValue))
                     .GroupBy(c => c.SiteValue!, StringComparer.OrdinalIgnoreCase))
            issues.Add(new SyncIssue("unmapped-site",
                $"Site value '{g.Key}' is not mapped to any site; {g.Count()} people synced without a site.", Count: g.Count()));
        var missingSite = valid.Values.Count(c => c.SiteValue is null);
        if (missingSite > 0)
            issues.Add(new SyncIssue("missing-site", $"{missingSite} people have no site value; synced without a site.",
                Count: missingSite));

        // 4. Safety checks against the current snapshot.
        var people = await db.People.ToListAsync(ct);
        var byId = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in people) byId.TryAdd(p.DirectoryId, p);

        var currentActive = people.Count(p => p.IsActive);
        var wouldDeactivate = people.Count(p => p.IsActive && !quarantinedIds.Contains(p.DirectoryId) &&
                                                (!valid.TryGetValue(p.DirectoryId, out var c) || !c.Enabled));
        var rejection =
            valid.Count == 0 ? "The directory returned no valid entries."
            : o.MinimumExpectedCount > 0 && valid.Count < o.MinimumExpectedCount
                ? $"Only {valid.Count} valid entries; at least {o.MinimumExpectedCount} expected."
            : o.MaxDropFraction is { } f && currentActive > 0 && wouldDeactivate > currentActive * f
                ? $"Run would deactivate {wouldDeactivate} of {currentActive} active people (limit {f:P0})."
            : null;
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
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Parses <see cref="SyncRun.IssuesJson"/> for display.</summary>
    public static IReadOnlyList<SyncIssue> ParseIssues(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<SyncIssue>>(json) ?? [];

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
