using System.Security.Claims;
using AssetTracker.Core.Data;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Web.Security;

/// <summary>
/// Derives app roles on every request. A Windows user's roles (IT, Manager, Admin, Facility) all come from the
/// application's own <c>RoleAssignments</c> (Admin → System roles), matched on the person's SID and only while the person
/// is active in the directory snapshot: one indexed join. AD group SIDs in the token are ignored. Role claims are never
/// stored in a cookie, so a change takes effect on the person's next request. Roles are a union; Admin implies nothing
/// else.
/// <para>The built-in super admin (identity from the <c>SuperAdmin</c> cookie scheme) gets exactly Admin, with no database
/// lookup: it has no SID and no groups, and it is not in <c>RoleAssignments</c>.</para>
/// </summary>
public class AppRoleClaimsTransformation(AppDbContext db) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == AppClaimTypes.RolesResolved))
            return principal;

        var roles = SuperAdminPrincipal.Is(principal) ? AppRoles.Admin : await AssignedRolesAsync(principal);
        var identity = new ClaimsIdentity();
        identity.AddClaim(new Claim(AppClaimTypes.RolesResolved, "1"));
        foreach (var role in Enum.GetValues<AppRoles>().Where(r => r != AppRoles.None && roles.HasFlag(r)))
            identity.AddClaim(new Claim(AppClaimTypes.Role, role.ToString()));

        var clone = principal.Clone();
        clone.AddIdentity(identity);
        return clone;
    }

    /// <summary>Roles an admin gave this person in the application (none for an inactive or unknown person).</summary>
    private async Task<AppRoles> AssignedRolesAsync(ClaimsPrincipal principal)
    {
        var sid = principal.FindFirst(ClaimTypes.PrimarySid)?.Value;
        if (string.IsNullOrEmpty(sid)) return AppRoles.None;
        var assigned = await (
            from r in db.RoleAssignments.AsNoTracking()
            join p in db.People.AsNoTracking() on r.PrincipalDirectoryId equals p.DirectoryId
            where p.Sid == sid && p.IsActive
            select r.Role).ToListAsync();
        return assigned.Aggregate(AppRoles.None, (acc, r) => acc | r);
    }
}
