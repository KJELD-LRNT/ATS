namespace AssetTracker.Web.Security;

/// <summary>
/// Roles used to come from AD group SIDs in the server config file (<c>Roles:ItGroupSids</c>, <c>Roles:ManagerGroupSids</c>,
/// <c>Roles:AdminGroupSids</c>). They are now given in the app (Admin → System roles) and those keys are ignored: startup
/// logs a warning and Admin → Settings shows a notice while the file still has them.
/// </summary>
public static class LegacyRoleSettings
{
    public const string Section = "Roles";

    public const string Notice =
        "The server config file still has Roles settings (AD group SIDs). They are ignored: roles are given in the app " +
        "under Admin → System roles. Remove the Roles section from the config file.";

    public static bool ArePresent(IConfiguration configuration) =>
        configuration.GetSection(Section).GetChildren().Any(c => c.Value is not null || c.GetChildren().Any());
}
