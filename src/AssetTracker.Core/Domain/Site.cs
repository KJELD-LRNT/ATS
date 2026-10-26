namespace AssetTracker.Core.Domain;

public class Site
{
    public int Id { get; set; }

    /// <summary>Stable short key, e.g. "AMS".</summary>
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Value of the AD site attribute that maps to this site. Null when unmapped.</summary>
    public string? DirectoryValue { get; set; }

    public bool IsActive { get; set; } = true;
}
