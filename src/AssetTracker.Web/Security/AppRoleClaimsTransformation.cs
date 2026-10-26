using System.Security.Claims;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AssetTracker.Web.Security;

/// <summary>
/// Derives app roles from the Windows token's group SIDs on every request, so removal from an AD group
/// takes effect as soon as the principal presents a new token. Roles are a union; Admin implies nothing else.
/// </summary>
public class AppRoleClaimsTransformation(IOptionsMonitor<RoleGroupOptions> options) : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == AppClaimTypes.Role))
            return Task.FromResult(principal);

        var roles = Resolve(principal, options.CurrentValue);
        var identity = new ClaimsIdentity();
        foreach (var role in Enum.GetValues<AppRoles>().Where(r => r != AppRoles.None && roles.HasFlag(r)))
            identity.AddClaim(new Claim(AppClaimTypes.Role, role.ToString()));

        var clone = principal.Clone();
        clone.AddIdentity(identity);
        return Task.FromResult(clone);
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
}
