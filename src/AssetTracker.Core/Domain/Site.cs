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

    /// <summary>Short code the tag generator puts where a type's pattern says <c>{SITE}</c> (e.g. "R" for Rotterdam).</summary>
    public string? TagCode { get; set; }

    /// <summary>
    /// IANA or Windows timezone id for this site's calendar (due dates, overdue rule, reminder time).
    /// Null = the default timezone (<c>Clock:TimeZoneId</c>). See <c>SiteCalendar</c>.
    /// </summary>
    public string? TimeZoneId { get; set; }
}
