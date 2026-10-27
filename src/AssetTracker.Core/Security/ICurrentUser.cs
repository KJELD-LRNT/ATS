namespace AssetTracker.Core.Security;

public interface ICurrentUser
{
    /// <summary>Windows account name (DOMAIN\user), used as audit actor.</summary>
    string Name { get; }

    /// <summary>Windows user SID; matches <see cref="Domain.Person.Sid"/>.</summary>
    string? Sid { get; }

    AppRoles Roles { get; }

    bool IsIt => Roles.HasFlag(AppRoles.It);
    bool IsManager => Roles.HasFlag(AppRoles.Manager);
    bool IsAdmin => Roles.HasFlag(AppRoles.Admin);
    bool IsFacility => Roles.HasFlag(AppRoles.Facility);
}
