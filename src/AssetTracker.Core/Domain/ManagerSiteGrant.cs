namespace AssetTracker.Core.Domain;

/// <summary>
/// Grants a manager (identified by Windows account / AD identity) read access to a site.
/// Has no effect unless the principal is currently in the manager AD group.
/// </summary>
public class ManagerSiteGrant
{
    public int Id { get; set; }

    /// <summary>Principal's stable AD identity (matches <see cref="Person.DirectoryId"/>).</summary>
    public string PrincipalDirectoryId { get; set; } = "";

    /// <summary>Display hint only (DOMAIN\user at grant time).</summary>
    public string PrincipalName { get; set; } = "";

    public int SiteId { get; set; }
    public Site? Site { get; set; }

    public DateTimeOffset GrantedAt { get; set; }
    public string GrantedBy { get; set; } = "";
}
