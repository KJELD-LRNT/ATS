using AssetTracker.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace AssetTracker.Infrastructure.Time;

public class ClockOptions
{
    /// <summary>
    /// IANA or Windows timezone id used for due dates and the daily overdue rule.
    /// Must be set to the client's agreed timezone (Stage 0); there is deliberately no default.
    /// </summary>
    public string TimeZoneId { get; set; } = "";
}

public class SystemClock(IOptions<ClockOptions> options) : IClock
{
    private readonly TimeZoneInfo _tz = Resolve(options.Value.TimeZoneId);

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly LocalToday => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(UtcNow, _tz).DateTime);

    private static TimeZoneInfo Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Clock:TimeZoneId is not configured. Set it to the client's agreed timezone.");
        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }
}
