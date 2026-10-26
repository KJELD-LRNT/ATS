using System.Reflection;

namespace AssetTracker.Core.Ops;

public static class AppVersion
{
    /// <summary>The assembly's informational version (e.g. <c>1.0.0+&lt;commit&gt;</c>), falling back to its version.</summary>
    public static string Of(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";
}
