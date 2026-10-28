namespace AssetTracker.Core.Security;

/// <summary>
/// Application roles. It, Manager and Admin are derived from AD security groups; Facility is assigned inside the
/// application (Admin → System roles, table <c>RoleAssignments</c>). Overlap is a union; Admin implies nothing else.
/// </summary>
[Flags]
public enum AppRoles
{
    None = 0,
    It = 1,
    Manager = 2,
    Admin = 4,

    /// <summary>Facility / operations: works with the Workplace floor plans. Assigned by an admin in the application.</summary>
    Facility = 8,
}

public static class AppClaimTypes
{
    /// <summary>Claim carrying an <see cref="AppRoles"/> name, added by claims transformation.</summary>
    public const string Role = "ats:role";

    /// <summary>Marks a principal whose roles were already resolved (so the lookup runs once per request).</summary>
    public const string RolesResolved = "ats:roles-resolved";
}

public static class Policies
{
    /// <summary>IT/editor: inventory and loan mutations.</summary>
    public const string ItOnly = "ItOnly";

    /// <summary>IT or manager: read inventory/reports (further scoped by site).</summary>
    public const string InventoryRead = "InventoryRead";

    /// <summary>Application admin: settings and grants.</summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>IT or application admin: read-only operational views such as directory sync history (D7).</summary>
    public const string ItOrAdmin = "ItOrAdmin";

    /// <summary>IT, facility or manager: look at the Workplace floor plans (managers only for their granted sites).</summary>
    public const string WorkplaceRead = "WorkplaceRead";

    /// <summary>IT or facility: import floor plans and change workspots and zones.</summary>
    public const string WorkplaceEdit = "WorkplaceEdit";

    /// <summary>Only the signed-in built-in super admin (its account page).</summary>
    public const string SuperAdminOnly = "SuperAdminOnly";
}
