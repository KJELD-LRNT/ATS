using AssetTracker.Core.Abstractions;

namespace AssetTracker.Infrastructure.Time;

/// <summary>
/// System time. Timezones live in <c>AssetTracker.Core.Time.SiteCalendar</c> (per site, default
/// <c>Clock:TimeZoneId</c> bound to <c>AssetTracker.Core.Time.ClockOptions</c>).
/// </summary>
public class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
