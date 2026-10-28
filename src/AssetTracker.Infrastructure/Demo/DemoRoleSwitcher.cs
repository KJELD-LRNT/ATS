using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Admin;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Infrastructure.Demo;

/// <summary>
/// The /Demo role switcher (demo mode only). The ticks <b>replace</b> the demo user's roles. It writes
/// <c>RoleAssignments</c> directly instead of going through <see cref="RoleAssignmentService"/>, because the switcher is
/// open to users with no role and that service requires Admin. Each change is still audited (actor
/// <see cref="DemoDefaults.SwitcherActor"/>), in the same save.
/// </summary>
public sealed class DemoRoleSwitcher(AppDbContext db, AuditWriter audit, IClock clock)
{
    /// <summary>The roles the switcher offers (all of them).</summary>
    public static IReadOnlyList<AppRoles> Roles => RoleAssignmentService.Assignable;

    /// <summary>The roles given to the active person with this SID (none for an unknown or inactive person).</summary>
    public async Task<AppRoles> RolesOfAsync(string sid, CancellationToken ct = default)
    {
        var roles = await (from r in db.RoleAssignments.AsNoTracking()
                           join p in db.People.AsNoTracking() on r.PrincipalDirectoryId equals p.DirectoryId
                           where p.Sid == sid && p.IsActive
                           select r.Role).ToListAsync(ct);
        return roles.Aggregate(AppRoles.None, (a, r) => a | r);
    }

    /// <summary>Sets the roles of the person with this SID to exactly <paramref name="roles"/>.</summary>
    public async Task ReplaceAsync(string sid, AppRoles roles, CancellationToken ct = default)
    {
        var person = await db.People.AsNoTracking().SingleOrDefaultAsync(p => p.Sid == sid, ct)
                     ?? throw new InvalidOperationException("No demo person has this SID.");
        var current = await db.RoleAssignments.Where(r => r.PrincipalDirectoryId == person.DirectoryId).ToListAsync(ct);
        var removed = current.Where(r => !roles.HasFlag(r.Role)).ToList();
        var added = Roles.Where(r => roles.HasFlag(r) && current.All(c => c.Role != r))
            .Select(r => new RoleAssignment
            {
                Role = r, PrincipalDirectoryId = person.DirectoryId, PrincipalName = person.Username,
                GrantedAt = clock.UtcNow, GrantedBy = DemoDefaults.SwitcherActor,
            }).ToList();
        if (removed.Count == 0 && added.Count == 0) return;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var r in removed)
        {
            db.RoleAssignments.Remove(r);
            audit.RecordAs(DemoDefaults.SwitcherActor, "role.remove", nameof(RoleAssignment), r.Id, null,
                details: new { Role = r.Role.ToString(), r.PrincipalDirectoryId, r.PrincipalName, r.GrantedAt, r.GrantedBy });
        }
        db.RoleAssignments.AddRange(added);
        await db.SaveChangesAsync(ct);
        foreach (var r in added)
            audit.RecordAs(DemoDefaults.SwitcherActor, "role.assign", nameof(RoleAssignment), r.Id, null,
                details: new { Role = r.Role.ToString(), PersonId = person.Id, person.DirectoryId, person.Username });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
