using AssetTracker.Core.Time;

namespace AssetTracker.Core.Notifications;

/// <summary>Pure scheduling rule for overdue reminders, evaluated per site in the site's own timezone.</summary>
public static class ReminderSchedule
{
    /// <summary>
    /// True when the site's local time of day has reached <paramref name="timeOfDay"/>. Once true it stays true for
    /// the rest of the site-local day, so a worker that was down at the reminder time catches up on its next tick;
    /// the per-day dedupe key keeps that to one reminder. On a DST transition day a reminder time inside the skipped
    /// hour is reached at the first local time after the gap, and a repeated hour cannot queue twice (same date).
    /// </summary>
    public static bool IsDue(SiteDay site, TimeOnly timeOfDay) => site.TimeOfDay >= timeOfDay;

    public static bool IsDue(DateTimeOffset utcNow, TimeZoneInfo zone, TimeOnly timeOfDay) =>
        IsDue(new SiteDay(0, zone, TimeZoneInfo.ConvertTime(utcNow, zone)), timeOfDay);
}
