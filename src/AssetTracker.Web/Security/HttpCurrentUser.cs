using System.Security.Claims;
using AssetTracker.Core.Security;

namespace AssetTracker.Web.Security;

public class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public string Name => Principal?.Identity?.Name ?? "anonymous";

    public string? Sid => Principal?.FindFirst(ClaimTypes.PrimarySid)?.Value;

    public AppRoles Roles => Principal is null
        ? AppRoles.None
        : Principal.FindAll(AppClaimTypes.Role)
            .Select(c => Enum.TryParse<AppRoles>(c.Value, out var r) ? r : AppRoles.None)
            .Aggregate(AppRoles.None, (acc, r) => acc | r);
}
