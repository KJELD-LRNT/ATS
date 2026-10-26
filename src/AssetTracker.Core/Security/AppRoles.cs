namespace AssetTracker.Core.Security;

/// <summary>Application roles derived from AD security groups. Overlap is a union; Admin implies nothing else.</summary>
[Flags]
public enum AppRoles
{
    None = 0,
    It = 1,
    Manager = 2,
    Admin = 4,
}

public static class AppClaimTypes
{
    /// <summary>Claim carrying an <see cref="AppRoles"/> name, added by claims transformation.</summary>
    public const string Role = "ats:role";
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
}
