using System.Net.Mail;
using System.Text.RegularExpressions;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Admin;

public sealed record GrantRow(int Id, int SiteId, string SiteKey, string SiteName, string PrincipalDirectoryId,
    string PrincipalName, string? PersonDisplayName, string? PersonUsername, bool? PersonActive,
    DateTimeOffset GrantedAt, string GrantedBy);

public sealed record PersonPick(int Id, string Username, string DisplayName, string? JobTitle, string? SiteKey, bool IsActive);

/// <summary>
/// Application-admin functions: sites and their AD mapping, manager site grants, settings, and manual sync.
/// Every method requires the admin role (which grants no inventory access by itself), except reading the sync
/// history, which IT may also do (D7). All changes are audited.
/// </summary>
public partial class AdminService(
    AppDbContext db,
    AccessScope scope,
    AuditWriter audit,
    ICurrentUser user,
    IClock clock,
    DirectorySyncService sync)
{
    public const int PeopleSearchLimit = 25;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex SiteKeyPattern();

    // ---- Sites -------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Site>> ListSitesAsync(CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        return await db.Sites.AsNoTracking().OrderBy(s => s.Key).ToListAsync(ct);
    }

    public async Task<Site> GetSiteAsync(int id, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        return await db.Sites.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct)
               ?? throw new DomainException("site.not-found", "Site not found.");
    }

    /// <param name="timeZoneId">IANA or Windows timezone id; empty = the default timezone (<c>Clock:TimeZoneId</c>).</param>
    public async Task<Site> CreateSiteAsync(string key, string name, string? directoryValue, bool isActive,
        string? timeZoneId = null, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        key = (key ?? "").Trim();
        if (!SiteKeyPattern().IsMatch(key))
            throw new DomainException("site.key", "Key must be 1-32 letters, digits, '-' or '_'.");
        name = RequireName(name);
        directoryValue = CleanDirectoryValue(directoryValue);
        timeZoneId = CleanTimeZoneId(timeZoneId);

        var keys = await db.Sites.Select(s => s.Key).ToListAsync(ct);
        if (keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException("site.key-taken", $"Site key '{key}' already exists.");
        await EnsureDirectoryValueFreeAsync(directoryValue, null, ct);

        // The audit row needs the generated id: two saves, one transaction.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var site = new Site
        {
            Key = key, Name = name, DirectoryValue = directoryValue, IsActive = isActive, TimeZoneId = timeZoneId,
        };
        db.Sites.Add(site);
        await db.SaveChangesAsync(ct);
        audit.Record("site.create", nameof(Site), site.Id, site.Id,
            details: new { site.Key, site.Name, site.DirectoryValue, site.IsActive, site.TimeZoneId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return site;
    }

    /// <summary>
    /// Updates name, AD mapping, active flag and timezone. The key is immutable once created.
    /// <paramref name="timeZoneId"/> empty/null = use the default timezone (<c>Clock:TimeZoneId</c>).
    /// </summary>
    public async Task<Site> UpdateSiteAsync(int id, string name, string? directoryValue, bool isActive,
        string? timeZoneId = null, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        var site = await db.Sites.SingleOrDefaultAsync(s => s.Id == id, ct)
                   ?? throw new DomainException("site.not-found", "Site not found.");
        name = RequireName(name);
        directoryValue = CleanDirectoryValue(directoryValue);
        timeZoneId = CleanTimeZoneId(timeZoneId);
        await EnsureDirectoryValueFreeAsync(directoryValue, id, ct);

        var before = new { site.Name, site.DirectoryValue, site.IsActive, site.TimeZoneId };
        site.Name = name;
        site.DirectoryValue = directoryValue;
        site.IsActive = isActive;
        site.TimeZoneId = timeZoneId;
        audit.Record("site.update", nameof(Site), site.Id, site.Id,
            details: new { before, after = new { site.Name, site.DirectoryValue, site.IsActive, site.TimeZoneId } });
        await db.SaveChangesAsync(ct);
        return site;
    }

    private static string RequireName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 200) throw new DomainException("site.name", "Name is required (max 200 characters).");
        return name;
    }

    private static string? CleanDirectoryValue(string? v)
    {
        v = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        if (v is { Length: > 200 }) throw new DomainException("site.directory-value", "Directory value is too long (max 200).");
        return v;
    }

    /// <summary>Empty = null (default timezone); otherwise must be a timezone id known on this server.</summary>
    private static string? CleanTimeZoneId(string? v)
    {
        v = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        if (v is null) return null;
        if (v.Length > 100 || !SiteCalendar.TryFindTimeZone(v, out _))
            throw new DomainException("site.timezone",
                $"'{(v.Length > 100 ? v[..100] : v)}' is not a known timezone. Use an IANA id such as Europe/Amsterdam or a Windows id such as W. Europe Standard Time, or leave it empty for the default timezone.");
        return v;
    }

    private async Task EnsureDirectoryValueFreeAsync(string? value, int? exceptSiteId, CancellationToken ct)
    {
        if (value is null) return;
        var used = await db.Sites.Where(s => s.DirectoryValue != null && s.Id != exceptSiteId)
            .Select(s => new { s.Key, s.DirectoryValue }).ToListAsync(ct);
        var clash = used.FirstOrDefault(s => string.Equals(s.DirectoryValue!.Trim(), value, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
            throw new ConflictException("site.directory-value-taken", $"Directory value '{value}' is already mapped to site {clash.Key}.");
    }

    // ---- Manager site grants -----------------------------------------------------------------------------

    public async Task<IReadOnlyList<GrantRow>> ListGrantsAsync(CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        var rows = await (
            from g in db.ManagerSiteGrants
            join s in db.Sites on g.SiteId equals s.Id
            join p in db.People on g.PrincipalDirectoryId equals p.DirectoryId into pj
            from p in pj.DefaultIfEmpty()
            orderby s.Key, g.PrincipalName
            select new GrantRow(g.Id, s.Id, s.Key, s.Name, g.PrincipalDirectoryId, g.PrincipalName,
                p == null ? null : p.DisplayName, p == null ? null : p.Username, p == null ? null : p.IsActive,
                g.GrantedAt, g.GrantedBy)).AsNoTracking().ToListAsync(ct);
        return rows;
    }

    /// <summary>
    /// Admin-only people lookup for choosing a grant principal. Managers never get a people search.
    /// Matches username or display name (case-insensitive substring), at most <see cref="PeopleSearchLimit"/> rows.
    /// </summary>
    public async Task<IReadOnlyList<PersonPick>> SearchPeopleAsync(string? query, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        query = query?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length < 2) return [];
        var pattern = "%" + EscapeLike(query.ToLowerInvariant()) + "%";
        return await db.People.AsNoTracking()
            .Where(p => EF.Functions.Like(p.Username.ToLower(), pattern, "\\") ||
                        EF.Functions.Like(p.DisplayName.ToLower(), pattern, "\\"))
            .OrderBy(p => p.DisplayName).ThenBy(p => p.Username)
            .Take(PeopleSearchLimit)
            .Select(p => new PersonPick(p.Id, p.Username, p.DisplayName, p.JobTitle,
                p.Site == null ? null : p.Site.Key, p.IsActive))
            .ToListAsync(ct);
    }

    public async Task<ManagerSiteGrant> AddGrantAsync(int personId, int siteId, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        var person = await db.People.AsNoTracking().SingleOrDefaultAsync(p => p.Id == personId, ct)
                     ?? throw new DomainException("grant.person", "Person not found.");
        if (!person.IsActive)
            throw new DomainException("grant.person-inactive", "Person is inactive in the directory.");
        var site = await db.Sites.AsNoTracking().SingleOrDefaultAsync(s => s.Id == siteId, ct)
                   ?? throw new DomainException("grant.site", "Site not found.");
        if (await db.ManagerSiteGrants.AnyAsync(g => g.PrincipalDirectoryId == person.DirectoryId && g.SiteId == siteId, ct))
            throw new ConflictException("grant.exists", $"{person.Username} already has a grant for site {site.Key}.");

        // The audit row needs the generated id: two saves, one transaction.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var grant = new ManagerSiteGrant
        {
            PrincipalDirectoryId = person.DirectoryId,
            PrincipalName = person.Username,
            SiteId = site.Id,
            GrantedAt = clock.UtcNow,
            GrantedBy = user.Name,
        };
        db.ManagerSiteGrants.Add(grant);
        await db.SaveChangesAsync(ct);
        audit.Record("grant.add", nameof(ManagerSiteGrant), grant.Id, site.Id,
            details: new { PersonId = person.Id, person.DirectoryId, person.Username, SiteKey = site.Key });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return grant;
    }

    /// <summary>Deletes the grant row; the audit event preserves who had it and who removed it.</summary>
    public async Task RemoveGrantAsync(int grantId, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        var grant = await db.ManagerSiteGrants.Include(g => g.Site).SingleOrDefaultAsync(g => g.Id == grantId, ct)
                    ?? throw new DomainException("grant.not-found", "Grant not found.");
        db.ManagerSiteGrants.Remove(grant);
        audit.Record("grant.remove", nameof(ManagerSiteGrant), grant.Id, grant.SiteId, details: new
        {
            grant.PrincipalDirectoryId, grant.PrincipalName, SiteKey = grant.Site?.Key, grant.GrantedAt, grant.GrantedBy,
        });
        await db.SaveChangesAsync(ct);
    }

    // ---- Settings ----------------------------------------------------------------------------------------

    public async Task<string?> GetItMailboxAsync(CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        return await db.Settings.AsNoTracking().Where(s => s.Key == AppSetting.ItMailbox)
            .Select(s => s.Value).SingleOrDefaultAsync(ct);
    }

    public async Task SetItMailboxAsync(string? address, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        address = address?.Trim() ?? "";
        if (!IsValidEmail(address))
            throw new DomainException("settings.it-mailbox", "Enter a single valid email address (e.g. it-desk@example.com).");

        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == AppSetting.ItMailbox, ct);
        var before = setting?.Value;
        if (setting is null)
        {
            setting = new AppSetting { Key = AppSetting.ItMailbox };
            db.Settings.Add(setting);
        }
        setting.Value = address;
        setting.UpdatedAt = clock.UtcNow;
        setting.UpdatedBy = user.Name;
        audit.Record("setting.update", nameof(AppSetting), AppSetting.ItMailbox, null,
            details: new { before, after = address });
        await db.SaveChangesAsync(ct);
    }

    public static bool IsValidEmail(string address) =>
        address.Length is > 0 and <= 320 &&
        MailAddress.TryCreate(address, out var parsed) &&
        parsed.Address == address && string.IsNullOrEmpty(parsed.DisplayName) &&
        parsed.Host.Contains('.') && !address.Contains(' ');

    // ---- Directory sync ----------------------------------------------------------------------------------

    /// <summary>Sync run history, newest first. IT or admin (sponsor decision D7: IT can view, read-only).</summary>
    public async Task<PagedResult<SyncRun>> ListSyncRunsAsync(int page, int pageSize = 20, CancellationToken ct = default)
    {
        if (!user.IsIt && !user.IsAdmin) throw new AccessDeniedException();
        page = Math.Max(1, page);
        var total = await db.SyncRuns.CountAsync(ct);
        var items = await db.SyncRuns.AsNoTracking().OrderByDescending(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<SyncRun>(items, total, page, pageSize);
    }

    /// <summary>Manual rerun, executed synchronously in the request. Admin only (IT views history read-only, D7).</summary>
    public Task<SyncRun> RunSyncNowAsync(CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        return sync.RunAsync(user.Name, ct);
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
