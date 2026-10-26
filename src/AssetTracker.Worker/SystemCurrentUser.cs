using AssetTracker.Core.Security;

namespace AssetTracker.Worker;

/// <summary>Actor identity for background jobs. Has no interactive roles; jobs call services that do not require them.</summary>
public class SystemCurrentUser : ICurrentUser
{
    public string Name => "system:worker";
    public string? Sid => null;
    public AppRoles Roles => AppRoles.None;
}
