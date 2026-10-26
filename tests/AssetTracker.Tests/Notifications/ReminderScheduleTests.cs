using AssetTracker.Core.Notifications;
using Microsoft.Extensions.Configuration;

namespace AssetTracker.Tests.Notifications;

public class ReminderScheduleTests
{
    private static readonly TimeZoneInfo Ams = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly TimeOnly Eight = new(8, 0);

    [Fact]
    public void Overdue_starts_the_day_after_the_due_date()
    {
        var today = new DateOnly(2026, 3, 10);
        Assert.False(OverduePolicy.IsOverdue(null, today));
        Assert.False(OverduePolicy.IsOverdue(today, today));
        Assert.True(OverduePolicy.IsOverdue(today.AddDays(-1), today));
        Assert.Equal(1, OverduePolicy.DaysOverdue(today.AddDays(-1), today));
    }

    [Fact]
    public void Runs_once_local_reminder_time_has_passed_and_not_yet_run()
    {
        // 06:30 UTC = 07:30 Amsterdam (winter): too early.
        Assert.False(ReminderSchedule.ShouldRunNow(new DateTimeOffset(2026, 1, 15, 6, 30, 0, TimeSpan.Zero), Ams, Eight, false));
        // 07:00 UTC = 08:00 local: due.
        Assert.True(ReminderSchedule.ShouldRunNow(new DateTimeOffset(2026, 1, 15, 7, 0, 0, TimeSpan.Zero), Ams, Eight, false));
        // Start-up catch-up later in the day, unless already run.
        Assert.True(ReminderSchedule.ShouldRunNow(new DateTimeOffset(2026, 1, 15, 15, 0, 0, TimeSpan.Zero), Ams, Eight, false));
        Assert.False(ReminderSchedule.ShouldRunNow(new DateTimeOffset(2026, 1, 15, 15, 0, 0, TimeSpan.Zero), Ams, Eight, true));
    }

    [Fact]
    public void Next_run_is_the_next_local_reminder_time_across_dst()
    {
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 7, 0, 0, TimeSpan.Zero),
            ReminderSchedule.NextRunUtc(new DateTimeOffset(2026, 1, 15, 6, 0, 0, TimeSpan.Zero), Ams, Eight));
        Assert.Equal(new DateTimeOffset(2026, 1, 16, 7, 0, 0, TimeSpan.Zero),
            ReminderSchedule.NextRunUtc(new DateTimeOffset(2026, 1, 15, 7, 0, 0, TimeSpan.Zero), Ams, Eight));
        // Summer time (UTC+2) starts 29 Mar 2026: 08:00 local on 30 Mar is 06:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 3, 30, 6, 0, 0, TimeSpan.Zero),
            ReminderSchedule.NextRunUtc(new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero), Ams, Eight));
    }

    [Fact]
    public void Options_bind_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:ReminderTimeOfDay"] = "07:30",
            ["Notifications:PollInterval"] = "00:01:00",
            ["Notifications:MaxAttempts"] = "5",
            ["Notifications:IncludePhoneNumberInMail"] = "true",
        }).Build();
        var o = config.GetSection(NotificationOptions.SectionName).Get<NotificationOptions>()!;
        Assert.Equal(new TimeOnly(7, 30), o.ReminderTimeOfDay);
        Assert.Equal(TimeSpan.FromMinutes(1), o.PollInterval);
        Assert.Equal(5, o.MaxAttempts);
        Assert.True(o.IncludePhoneNumberInMail);
        Assert.False(new NotificationOptions().IncludePhoneNumberInMail);
    }
}
