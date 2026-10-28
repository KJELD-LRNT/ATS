namespace AssetTracker.Core.Domain;

/// <summary>
/// An account outside AD that signs in with a username and password. Exactly one exists: the built-in super admin
/// (<see cref="Security.SuperAdmin.UserName"/>), created by the setup command on the server (or the demo seed). It is
/// not in <c>RoleAssignments</c>: it always gets the Admin role and nothing else, and no admin can remove it.
/// </summary>
public class LocalAccount
{
    public int Id { get; set; }

    public string UserName { get; set; } = "";

    /// <summary>ASP.NET Core Identity <c>PasswordHasher</c> hash (PBKDF2, format V3). Null until a password is set.</summary>
    public string? PasswordHash { get; set; }

    public DateTimeOffset? PasswordChangedAt { get; set; }

    /// <summary>Off until a password is set; the setup command's <c>disable</c> switches it off again.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Failed sign-in attempts since the last success, unlock or new password (incremented atomically).</summary>
    public int FailedCount { get; set; }

    /// <summary>Set when <see cref="Security.SuperAdmin.MaxFailedAttempts"/> attempts failed; sign-in is refused until then.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }
    public string? LastSignInIp { get; set; }

    /// <summary>Changes on every password change, reset and disable; a session whose stamp differs ends.</summary>
    public string SecurityStamp { get; set; } = "";
}
