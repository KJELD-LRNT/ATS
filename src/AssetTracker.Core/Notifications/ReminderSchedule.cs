namespace AssetTracker.Core.Notifications;

/// <summary>Pure scheduling math for the daily overdue-reminder job (client-local time of day).</summary>
public static class ReminderSchedule
{
    /// <summary>
    /// True when the local time has reached today's reminder time and today's run has not happened yet.
    /// This also gives start-up catch-up: a worker started after the reminder time runs immediately.
    /// </summary>
    public static bool ShouldRunNow(DateTimeOffset utcNow, TimeZoneInfo tz, TimeOnly timeOfDay, bool alreadyRanToday)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, tz);
        return !alreadyRanToday && TimeOnly.FromDateTime(local.DateTime) >= timeOfDay;
    }

    /// <summary>Next UTC instant at which local time equals <paramref name="timeOfDay"/>, strictly after now.</summary>
    public static DateTimeOffset NextRunUtc(DateTimeOffset utcNow, TimeZoneInfo tz, TimeOnly timeOfDay)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, tz);
        var date = DateOnly.FromDateTime(local.DateTime);
        for (var i = 0; i < 3; i++)
        {
            var candidateLocal = date.AddDays(i).ToDateTime(timeOfDay, DateTimeKind.Unspecified);
            // A time inside a DST gap does not exist locally; use the first valid instant after it.
            if (tz.IsInvalidTime(candidateLocal)) candidateLocal = candidateLocal.AddHours(1);
            var candidateUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(candidateLocal, tz), TimeSpan.Zero);
            if (candidateUtc > utcNow) return candidateUtc;
        }
        return utcNow.AddDays(1);
    }
}
