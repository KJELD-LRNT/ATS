using AssetTracker.Core.Security;

namespace AssetTracker.Infrastructure.Demo;

/// <summary>
/// Demo mode only: an identity that applies to the current async flow, used to seed or reset demo data as a
/// system IT/admin actor through the real services (so audit and outbox rows are consistent). The demo
/// <c>ICurrentUser</c> consults it before the HTTP user.
/// </summary>
public static class AmbientCurrentUser
{
    private static readonly AsyncLocal<ICurrentUser?> Value = new();

    public static ICurrentUser? Current => Value.Value;

    /// <summary>Runs <paramref name="action"/> as <paramref name="name"/> with <paramref name="roles"/>.</summary>
    public static async Task<T> RunAsAsync<T>(string name, AppRoles roles, Func<Task<T>> action)
    {
        var previous = Value.Value;
        Value.Value = new FixedUser(name, null, roles);
        try { return await action(); }
        finally { Value.Value = previous; }
    }

    public static Task RunAsAsync(string name, AppRoles roles, Func<Task> action) =>
        RunAsAsync<object?>(name, roles, async () => { await action(); return null; });

    private sealed record FixedUser(string Name, string? Sid, AppRoles Roles) : ICurrentUser;
}

/// <summary>Background-job identity in the demo web process (same name as the production worker).</summary>
public sealed class DemoSystemUser : ICurrentUser
{
    public static readonly DemoSystemUser Instance = new();

    public string Name => "system:worker";
    public string? Sid => null;
    public AppRoles Roles => AppRoles.None;
}
