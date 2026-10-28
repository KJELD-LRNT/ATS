using System.Security.Claims;
using AssetTracker.Core.Data;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AssetTracker.Web.Security;

/// <summary>
/// Derives app roles on every request: It, Manager and Admin from the Windows token's group SIDs, so removal from an
/// AD group takes effect as soon as the principal presents a new token; Facility from the application's own
/// <c>RoleAssignments</c> (matched on the person's SID, and only while the person is active). Roles are a union; Admin
/// implies nothing else.
/// <para>The built-in super admin (identity from the <c>SuperAdmin</c> cookie scheme) gets exactly Admin, with no database
/// lookup: it has no SID and no groups, and it is not in <c>RoleAssignments</c>.</para>
/// </summary>
public class AppRoleClaimsTransformation(IOptionsMonitor<RoleGroupOptions> options, AppDbContext db) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == AppClaimTypes.RolesResolved))
            return principal;

        var roles = SuperAdminPrincipal.Is(principal)
            ? AppRoles.Admin
            : Resolve(principal, options.CurrentValue) | await AssignedRolesAsync(principal);
        var identity = new ClaimsIdentity();
        identity.AddClaim(new Claim(AppClaimTypes.RolesResolved, "1"));
        foreach (var role in Enum.GetValues<AppRoles>().Where(r => r != AppRoles.None && roles.HasFlag(r)))
            identity.AddClaim(new Claim(AppClaimTypes.Role, role.ToString()));

        var clone = principal.Clone();
        clone.AddIdentity(identity);
        return clone;
    }

    public static AppRoles Resolve(ClaimsPrincipal principal, RoleGroupOptions o)
    {
        var groups = principal.FindAll(ClaimTypes.GroupSid).Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roles = AppRoles.None;
        if (o.ItGroupSids.Any(groups.Contains)) roles |= AppRoles.It;
        if (o.ManagerGroupSids.Any(groups.Contains)) roles |= AppRoles.Manager;
        if (o.AdminGroupSids.Any(groups.Contains)) roles |= AppRoles.Admin;
        return roles;
    }

    /// <summary>Roles an admin gave this person inside the application (none for an inactive or unknown person).</summary>
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
