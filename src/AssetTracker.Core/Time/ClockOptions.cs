namespace AssetTracker.Core.Time;

/// <summary>Bound from the "Clock" configuration section.</summary>
public class ClockOptions
{
    public const string SectionName = "Clock";

    /// <summary>
    /// IANA or Windows timezone id of the <b>default</b> site calendar: used for every site whose
    /// <see cref="Domain.Site.TimeZoneId"/> is empty. Must be set to the client's agreed timezone (Stage 0);
    /// there is deliberately no default. Read through <c>IOptionsMonitor</c>, so a changed value applies live.
    /// </summary>
    public string TimeZoneId { get; set; } = "";
}
