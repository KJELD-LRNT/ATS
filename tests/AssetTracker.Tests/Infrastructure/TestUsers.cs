using AssetTracker.Core.Security;

namespace AssetTracker.Tests.Infrastructure;

/// <summary>
/// A simulated Windows principal (name and SID; no groups, since roles are not read from the token). A user with
/// <see cref="Roles"/> is an active person in the directory snapshot with those roles in <c>RoleAssignments</c>:
/// <c>AppFactory</c> makes the person (or uses the one with this SID) and the role assignments the first time the
/// user signs in on that factory, and keeps the person it made in its fake directory.
/// </summary>
public sealed record TestUser(string Name, string Sid, AppRoles Roles = AppRoles.None)
{
    public static readonly TestUser It = new(@"CORP\it.user", "S-1-5-21-1000-1000-1000-2001", AppRoles.It);
    public static readonly TestUser Manager = new(@"CORP\manager.user", "S-1-5-21-1000-1000-1000-2002", AppRoles.Manager);
    public static readonly TestUser Admin = new(@"CORP\admin.user", "S-1-5-21-1000-1000-1000-2003", AppRoles.Admin);
    public static readonly TestUser Nobody = new(@"CORP\plain.user", "S-1-5-21-1000-1000-1000-2004");
    public static readonly TestUser Facility = new(@"CORP\facility.user", "S-1-5-21-1000-1000-1000-2005", AppRoles.Facility);

    /// <summary>The username part (<c>CORP\it.user</c> → <c>it.user</c>).</summary>
    public string Username => Name.Split('\\').Last();
}
