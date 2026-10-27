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

/// <summary>
/// Roles that live in the application instead of in AD groups (today: <see cref="AppRoles.Facility"/>). Admin only,
/// audited. An assignment only counts while the person is active in the directory snapshot, like manager grants.
/// </summary>
public class RoleAssignmentService(AppDbContext db, AccessScope scope, AuditWriter audit, ICurrentUser user, IClock clock)
{
    /// <summary>The roles an admin can give here.</summary>
    public static readonly IReadOnlyList<AppRoles> Assignable = [AppRoles.Facility];

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

    public async Task<RoleAssignment> AssignAsync(int personId, AppRoles role, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        if (!Assignable.Contains(role))
            throw new DomainException("role.not-assignable", $"The {role} role comes from an AD group and cannot be assigned here.");
        var person = await db.People.AsNoTracking().SingleOrDefaultAsync(p => p.Id == personId, ct)
                     ?? throw new DomainException("role.person", "Person not found.");
        if (!person.IsActive)
            throw new DomainException("role.person-inactive", "Person is inactive in the directory.");
        if (string.IsNullOrEmpty(person.Sid))
            throw new DomainException("role.person-sid",
                "This person has no Windows account (SID) in the directory snapshot, so they cannot sign in with a role.");
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
        audit.Record("role.assign", nameof(RoleAssignment), row.Id, null,
            details: new { Role = role.ToString(), PersonId = person.Id, person.DirectoryId, person.Username });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task RemoveAsync(int assignmentId, CancellationToken ct = default)
    {
        scope.EnsureAdmin();
        var row = await db.RoleAssignments.SingleOrDefaultAsync(r => r.Id == assignmentId, ct)
                  ?? throw new DomainException("role.not-found", "Role assignment not found.");
        db.RoleAssignments.Remove(row);
        audit.Record("role.remove", nameof(RoleAssignment), row.Id, null, details: new
        {
            Role = row.Role.ToString(), row.PrincipalDirectoryId, row.PrincipalName, row.GrantedAt, row.GrantedBy,
        });
        await db.SaveChangesAsync(ct);
    }
}
