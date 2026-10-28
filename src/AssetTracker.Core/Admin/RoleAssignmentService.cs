using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Admin;

public sealed record RoleAssignmentRow(int Id, AppRoles Role, string PrincipalDirectoryId, string PrincipalName,
    string? PersonDisplayName, string? PersonUsername, bool? PersonActive, DateTimeOffset GrantedAt, string GrantedBy);

/// <summary>A manager site grant shown on a person's row of the System roles page.</summary>
public sealed record PersonGrant(int GrantId, int SiteId, string SiteKey, string SiteName);

/// <summary>
/// One person on the System roles page: their roles and manager site grants. <see cref="PersonId"/> is null when the
/// directory id is no longer in the directory snapshot (then only removing is possible).
/// </summary>
public sealed record PersonRolesRow(
    string DirectoryId,
    int? PersonId,
    string DisplayName,
    string Username,
    string? JobTitle,
    string? SiteKey,
    int? SiteId,
    bool IsActive,
    bool HasSid,
    AppRoles Roles,
    IReadOnlyList<PersonGrant> Grants,
    bool IsCurrentUser)
{
    public bool InSnapshot => PersonId is not null;

    /// <summary>A role needs an active person with a Windows account, else it could never be used to sign in.</summary>
    public bool CanGetRole => InSnapshot && IsActive && HasSid;

    /// <summary>A site grant needs an active person (no Windows account needed).</summary>
    public bool CanGetGrant => InSnapshot && IsActive;
}

/// <summary>Filters of the System roles page.</summary>
public sealed record RoleFilter(AppRoles Role = AppRoles.None, int? SiteId = null, bool ShowInactive = false);

/// <summary>What a save on the System roles page changed.</summary>
public sealed record RoleChange(
    string PersonName,
    AppRoles Added,
    AppRoles Removed,
    IReadOnlyList<string> SitesAdded,
    IReadOnlyList<string> SitesRemoved,
    bool NoAdminLeft)
{
    public bool Changed => Added != AppRoles.None || Removed != AppRoles.None || SitesAdded.Count > 0 || SitesRemoved.Count > 0;
}

/// <summary>
/// Every application role (IT, Manager, Admin, Facility) is given to a person here, inside the application, together
/// with the manager site grants. Admin only (the super admin has the Admin role), audited (<c>role.assign</c>,
/// <c>role.remove</c>, <c>grant.add</c>, <c>grant.remove</c>). A role only counts while the person is active in the
/// directory snapshot (<c>AppRoleClaimsTransformation</c> matches the person's SID); a grant only counts while the
/// person also has the Manager role and is active (sponsor decision D9).
/// <para>Safety rules: an admin cannot remove their own Admin role; the super admin (no SID) may remove any role,
/// including the last admin's (<see cref="RoleChange.NoAdminLeft"/> tells the page to warn).</para>
/// </summary>
public class RoleAssignmentService(AppDbContext db, AccessScope scope, AuditWriter audit, ICurrentUser user, IClock clock)
{
    /// <summary>The roles an admin can give here: all of them.</summary>
    public static readonly IReadOnlyList<AppRoles> Assignable = [AppRoles.It, AppRoles.Manager, AppRoles.Admin, AppRoles.Facility];

    private static readonly AppRoles AssignableMask = Assignable.Aggregate(AppRoles.None, (a, r) => a | r);

    public const int SearchLimit = AdminService.PeopleSearchLimit;

    public async Task<IReadOnlyList<RoleAssignmentRow>> ListAsync(CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        return await (
            from r in db.RoleAssignments.AsNoTracking()
            join p in db.People.AsNoTracking() on r.PrincipalDirectoryId equals p.DirectoryId into people
            from p in people.DefaultIfEmpty()
            orderby r.Role, p.DisplayName, r.PrincipalName
            select new RoleAssignmentRow(r.Id, r.Role, r.PrincipalDirectoryId, r.PrincipalName,
                p == null ? null : p.DisplayName, p == null ? null : p.Username, p == null ? (bool?)null : p.IsActive,
                r.GrantedAt, r.GrantedBy)).ToListAsync(ct);
    }

    /// <summary>
    /// One row per person with any role or manager site grant, filtered. Inactive people (and directory ids no longer in
    /// the snapshot) are left out unless <see cref="RoleFilter.ShowInactive"/>. The site filter matches a grant for the
    /// site or the person's own (directory) site.
    /// </summary>
    public async Task<IReadOnlyList<PersonRolesRow>> PeopleAsync(RoleFilter filter, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        var roles = await db.RoleAssignments.AsNoTracking()
            .Select(r => new { r.PrincipalDirectoryId, r.PrincipalName, r.Role }).ToListAsync(ct);
        var grants = await GrantsAsync(null, ct);
        var ids = roles.Select(r => r.PrincipalDirectoryId).Concat(grants.Select(g => g.DirectoryId)).ToHashSet();
        var people = await PeopleByDirectoryIdAsync(ids, ct);

        var rows = ids.Select(id =>
        {
            people.TryGetValue(id, out var p);
            var name = roles.FirstOrDefault(r => r.PrincipalDirectoryId == id)?.PrincipalName
                       ?? grants.First(g => g.DirectoryId == id).PrincipalName;
            return ToRow(id, p, name,
                roles.Where(r => r.PrincipalDirectoryId == id).Aggregate(AppRoles.None, (a, r) => a | r.Role),
                grants.Where(g => g.DirectoryId == id).Select(g => g.Grant).ToList());
        });

        if (!filter.ShowInactive) rows = rows.Where(r => r.InSnapshot && r.IsActive);
        if (filter.Role != AppRoles.None) rows = rows.Where(r => r.Roles.HasFlag(filter.Role));
        if (filter.SiteId is { } siteId) rows = rows.Where(r => r.SiteId == siteId || r.Grants.Any(g => g.SiteId == siteId));
        return rows.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Username).ToList();
    }

    /// <summary>
    /// <i>Add a person</i>: people in the directory snapshot whose name or username contains the text (at least 2
    /// characters, at most <see cref="SearchLimit"/>), with the roles and grants they already have.
    /// </summary>
    public async Task<IReadOnlyList<PersonRolesRow>> SearchAsync(string? query, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        query = query?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length < 2) return [];
        var pattern = "%" + AdminService.EscapeLike(query.ToLowerInvariant()) + "%";
        var people = await db.People.AsNoTracking().Include(p => p.Site)
            .Where(p => EF.Functions.Like(p.Username.ToLower(), pattern, "\\") ||
                        EF.Functions.Like(p.DisplayName.ToLower(), pattern, "\\"))
            .OrderBy(p => p.DisplayName).ThenBy(p => p.Username)
            .Take(SearchLimit).ToListAsync(ct);
        var ids = people.Select(p => p.DirectoryId).ToList();
        var roles = await db.RoleAssignments.AsNoTracking().Where(r => ids.Contains(r.PrincipalDirectoryId))
            .Select(r => new { r.PrincipalDirectoryId, r.Role }).ToListAsync(ct);
        var grants = await GrantsAsync(ids, ct);
        return people.Select(p => ToRow(p.DirectoryId, p, p.Username,
                roles.Where(r => r.PrincipalDirectoryId == p.DirectoryId).Aggregate(AppRoles.None, (a, r) => a | r.Role),
                grants.Where(g => g.DirectoryId == p.DirectoryId).Select(g => g.Grant).ToList()))
            .ToList();
    }

    /// <summary>
    /// Sets a person's roles and manager site grants to exactly <paramref name="roles"/> and <paramref name="siteIds"/>:
    /// adds and removes the difference in one transaction, with one audit row per change.
    /// </summary>
    public async Task<RoleChange> SaveAsync(string directoryId, AppRoles roles, IReadOnlyCollection<int> siteIds,
        CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        if ((roles & ~AssignableMask) != AppRoles.None)
            throw new DomainException("role.not-assignable", "That role does not exist.");
        directoryId = (directoryId ?? "").Trim();
        var person = await db.People.AsNoTracking().SingleOrDefaultAsync(p => p.DirectoryId == directoryId, ct);
        var currentRoles = await db.RoleAssignments.Where(r => r.PrincipalDirectoryId == directoryId).ToListAsync(ct);
        var currentGrants = await db.ManagerSiteGrants.Include(g => g.Site)
            .Where(g => g.PrincipalDirectoryId == directoryId).ToListAsync(ct);
        if (person is null && currentRoles.Count == 0 && currentGrants.Count == 0)
            throw new DomainException("role.person", "Person not found.");
        var name = person?.DisplayName
                   ?? currentRoles.Select(r => r.PrincipalName).Concat(currentGrants.Select(g => g.PrincipalName)).First();

        var had = currentRoles.Aggregate(AppRoles.None, (a, r) => a | r.Role);
        var added = roles & ~had;
        var removed = had & ~roles;
        if (added != AppRoles.None) EnsureCanGetRole(person);
        if (removed.HasFlag(AppRoles.Admin)) EnsureNotOwnAdmin(person);

        var wanted = siteIds.ToHashSet();
        var grantedSites = currentGrants.Select(g => g.SiteId).ToHashSet();
        var sitesToAdd = wanted.Where(s => !grantedSites.Contains(s)).ToList();
        var grantsToRemove = currentGrants.Where(g => !wanted.Contains(g.SiteId)).ToList();
        var newSites = await db.Sites.AsNoTracking().Where(s => sitesToAdd.Contains(s.Id)).ToListAsync(ct);
        if (newSites.Count != sitesToAdd.Count) throw new DomainException("grant.site", "Site not found.");
        if (sitesToAdd.Count > 0) EnsureCanGetGrant(person);

        if (added == AppRoles.None && removed == AppRoles.None && sitesToAdd.Count == 0 && grantsToRemove.Count == 0)
            return new RoleChange(name, AppRoles.None, AppRoles.None, [], [], !await AnyAdminLeftAsync(ct));

        // New rows need their generated ids in the audit: two saves, one transaction.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;
        var newRoles = Split(added).Select(r => new RoleAssignment
        {
            Role = r, PrincipalDirectoryId = person!.DirectoryId, PrincipalName = person.Username, GrantedAt = now,
            GrantedBy = user.Name,
        }).ToList();
        var newGrants = newSites.Select(s => (Site: s, Grant: new ManagerSiteGrant
        {
            PrincipalDirectoryId = person!.DirectoryId, PrincipalName = person.Username, SiteId = s.Id, GrantedAt = now,
            GrantedBy = user.Name,
        })).ToList();
        db.RoleAssignments.AddRange(newRoles);
        db.ManagerSiteGrants.AddRange(newGrants.Select(g => g.Grant));
        foreach (var row in currentRoles.Where(r => removed.HasFlag(r.Role)))
        {
            db.RoleAssignments.Remove(row);
            RecordRemoved(row);
        }
        foreach (var g in grantsToRemove)
        {
            db.ManagerSiteGrants.Remove(g);
            RecordGrantRemoved(g);
        }
        try
        {
            await db.SaveChangesAsync(ct);
            foreach (var row in newRoles) RecordAssigned(row, person!);
            foreach (var (site, grant) in newGrants)
                audit.Record("grant.add", nameof(ManagerSiteGrant), grant.Id, site.Id,
                    details: new { PersonId = person!.Id, person.DirectoryId, person.Username, SiteKey = site.Key });
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("role.concurrent",
                $"Someone else changed the roles of {name} at the same time. Reload the page and try again.");
        }
        await tx.CommitAsync(ct);

        return new RoleChange(name, added, removed,
            newSites.Select(s => s.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            grantsToRemove.Select(g => g.Site?.Key ?? g.SiteId.ToString()).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            !await AnyAdminLeftAsync(ct));
    }

    public async Task<RoleAssignment> AssignAsync(int personId, AppRoles role, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        if (!Assignable.Contains(role))
            throw new DomainException("role.not-assignable", "That role does not exist.");
        var person = await db.People.AsNoTracking().SingleOrDefaultAsync(p => p.Id == personId, ct)
                     ?? throw new DomainException("role.person", "Person not found.");
        EnsureCanGetRole(person);
        if (await db.RoleAssignments.AnyAsync(r => r.Role == role && r.PrincipalDirectoryId == person.DirectoryId, ct))
            throw new ConflictException("role.exists", $"{person.Username} already has the {role} role.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = new RoleAssignment
        {
            Role = role, PrincipalDirectoryId = person.DirectoryId, PrincipalName = person.Username,
            GrantedAt = clock.UtcNow, GrantedBy = user.Name,
        };
        db.RoleAssignments.Add(row);
        await db.SaveChangesAsync(ct);
        RecordAssigned(row, person);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task RemoveAsync(int assignmentId, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        var row = await db.RoleAssignments.SingleOrDefaultAsync(r => r.Id == assignmentId, ct)
                  ?? throw new DomainException("role.not-found", "Role assignment not found.");
        if (row.Role == AppRoles.Admin)
            EnsureNotOwnAdmin(await db.People.AsNoTracking().SingleOrDefaultAsync(p => p.DirectoryId == row.PrincipalDirectoryId, ct));
        db.RoleAssignments.Remove(row);
        RecordRemoved(row);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Whether any active person with a Windows account still has the Admin role. When none has, only the super admin
    /// can give roles.
    /// </summary>
    public async Task<bool> AnyAdminLeftAsync(CancellationToken ct = default) =>
        await (from r in db.RoleAssignments
               join p in db.People on r.PrincipalDirectoryId equals p.DirectoryId
               where r.Role == AppRoles.Admin && p.IsActive && p.Sid != null
               select r.Id).AnyAsync(ct);

    private static void EnsureCanGetRole(Person? person)
    {
        if (person is null)
            throw new DomainException("role.person", "This person is no longer in the directory, so they can't get a role.");
        if (!person.IsActive)
            throw new DomainException("role.person-inactive",
                $"{person.DisplayName} is inactive in the directory, so they can't get a role.");
        if (string.IsNullOrEmpty(person.Sid))
            throw new DomainException("role.person-sid",
                $"{person.DisplayName} has no Windows account in the directory, so they could never sign in with a role.");
    }

    private static void EnsureCanGetGrant(Person? person)
    {
        if (person is null)
            throw new DomainException("grant.person", "This person is no longer in the directory, so they can't get a site.");
        if (!person.IsActive)
            throw new DomainException("grant.person-inactive",
                $"{person.DisplayName} is inactive in the directory, so they can't get a site.");
    }

    private void EnsureNotOwnAdmin(Person? person)
    {
        if (person?.Sid is { Length: > 0 } sid && string.Equals(sid, user.Sid, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("role.own-admin", "You can't remove your own Admin role. Ask another admin to do it.");
    }

    private void RecordAssigned(RoleAssignment row, Person person) =>
        audit.Record("role.assign", nameof(RoleAssignment), row.Id, null,
            details: new { Role = row.Role.ToString(), PersonId = person.Id, person.DirectoryId, person.Username });

    private void RecordRemoved(RoleAssignment row) =>
        audit.Record("role.remove", nameof(RoleAssignment), row.Id, null, details: new
        {
            Role = row.Role.ToString(), row.PrincipalDirectoryId, row.PrincipalName, row.GrantedAt, row.GrantedBy,
        });

    private void RecordGrantRemoved(ManagerSiteGrant g) =>
        audit.Record("grant.remove", nameof(ManagerSiteGrant), g.Id, g.SiteId, details: new
        {
            g.PrincipalDirectoryId, g.PrincipalName, SiteKey = g.Site?.Key, g.GrantedAt, g.GrantedBy,
        });

    private static IEnumerable<AppRoles> Split(AppRoles roles) => Assignable.Where(r => roles.HasFlag(r));

    private PersonRolesRow ToRow(string directoryId, Person? p, string fallbackName, AppRoles roles,
        IReadOnlyList<PersonGrant> grants) =>
        new(directoryId, p?.Id, p?.DisplayName ?? fallbackName, p?.Username ?? fallbackName, p?.JobTitle, p?.Site?.Key,
            p?.SiteId, p?.IsActive ?? false, !string.IsNullOrEmpty(p?.Sid), roles,
            grants.OrderBy(g => g.SiteKey, StringComparer.OrdinalIgnoreCase).ToList(),
            p?.Sid is { Length: > 0 } sid && string.Equals(sid, user.Sid, StringComparison.OrdinalIgnoreCase));

    private async Task<Dictionary<string, Person>> PeopleByDirectoryIdAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        return await db.People.AsNoTracking().Include(p => p.Site).Where(p => list.Contains(p.DirectoryId))
            .ToDictionaryAsync(p => p.DirectoryId, ct);
    }

    private sealed record GrantOf(string DirectoryId, string PrincipalName, PersonGrant Grant);

    private async Task<List<GrantOf>> GrantsAsync(IReadOnlyCollection<string>? directoryIds, CancellationToken ct)
    {
        var q = db.ManagerSiteGrants.AsNoTracking();
        if (directoryIds is not null)
        {
            var list = directoryIds.ToList();
            q = q.Where(g => list.Contains(g.PrincipalDirectoryId));
        }
        var rows = await q.Join(db.Sites, g => g.SiteId, s => s.Id,
                (g, s) => new { g.PrincipalDirectoryId, g.PrincipalName, g.Id, SiteId = s.Id, s.Key, s.Name })
            .ToListAsync(ct);
        return rows.Select(r => new GrantOf(r.PrincipalDirectoryId, r.PrincipalName, new PersonGrant(r.Id, r.SiteId, r.Key, r.Name)))
            .ToList();
    }
}
