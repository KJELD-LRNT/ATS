using AssetTracker.Core.Security;

namespace AssetTracker.Core.Domain;

/// <summary>
/// A role given to a person inside the application (not from an AD group): today only <see cref="AppRoles.Facility"/>.
/// Like manager grants it points at the person's stable directory id and only counts while that person is active in
/// the directory snapshot.
/// </summary>
public class RoleAssignment
{
    public int Id { get; set; }

    public AppRoles Role { get; set; }

    /// <summary>Principal's stable AD identity (matches <see cref="Person.DirectoryId"/>).</summary>
    public string PrincipalDirectoryId { get; set; } = "";

    /// <summary>Display hint only (username at assignment time).</summary>
    public string PrincipalName { get; set; } = "";

    public DateTimeOffset GrantedAt { get; set; }
    public string GrantedBy { get; set; } = "";
}
