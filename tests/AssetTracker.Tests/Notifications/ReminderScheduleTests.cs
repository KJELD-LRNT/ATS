using AssetTracker.Core.Notifications;
using Microsoft.Extensions.Configuration;

namespace AssetTracker.Tests.Notifications;

public class ReminderScheduleTests
{
    private static readonly TimeZoneInfo Ams = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly TimeOnly Eight = new(8, 0);

    /// <summary>Sponsor decision D4: overdue from the due date itself; days overdue counts days past due.</summary>
    [Fact]
    public void Overdue_starts_on_the_due_date()
    {
        var today = new DateOnly(2026, 3, 10);
        Assert.False(OverduePolicy.IsOverdue(null, today));
        Assert.False(OverduePolicy.IsOverdue(today.AddDays(1), today));
        Assert.True(OverduePolicy.IsOverdue(today, today));
        Assert.Equal(0, OverduePolicy.DaysOverdue(today, today));
        Assert.True(OverduePolicy.IsOverdue(today.AddDays(-1), today));
        Assert.Equal(1, OverduePolicy.DaysOverdue(today.AddDays(-1), today));
        Assert.Equal(today, OverduePolicy.LatestOverdueDueDate(today));
    }

    [Fact]
    public void Reminders_are_due_from_the_local_reminder_time_for_the_rest_of_the_day()
    {
        // 06:30 UTC = 07:30 Amsterdam (winter): too early.
        Assert.False(ReminderSchedule.IsDue(new DateTimeOffset(2026, 1, 15, 6, 30, 0, TimeSpan.Zero), Ams, Eight));
        // 07:00 UTC = 08:00 local: due.
        Assert.True(ReminderSchedule.IsDue(new DateTimeOffset(2026, 1, 15, 7, 0, 0, TimeSpan.Zero), Ams, Eight));
        // Later the same day (e.g. a worker that was down at 08:00): still due; dedupe keeps it to one reminder.
        Assert.True(ReminderSchedule.IsDue(new DateTimeOffset(2026, 1, 15, 22, 59, 0, TimeSpan.Zero), Ams, Eight));
        // 23:00 UTC = 00:00 local on the 16th: a new local day, not yet due.
        Assert.False(ReminderSchedule.IsDue(new DateTimeOffset(2026, 1, 15, 23, 0, 0, TimeSpan.Zero), Ams, Eight));
    }

    [Fact]
    public void Reminder_time_inside_a_dst_gap_is_reached_after_the_gap()
    {
        // Summer time starts 29 Mar 2026 in Amsterdam: 02:00 local jumps to 03:00, so 02:30 never happens.
        var halfPastTwo = new TimeOnly(2, 30);
        Assert.False(ReminderSchedule.IsDue(new DateTimeOffset(2026, 3, 29, 0, 59, 0, TimeSpan.Zero), Ams, halfPastTwo)); // 01:59 CET
        Assert.True(ReminderSchedule.IsDue(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), Ams, halfPastTwo)); // 03:00 CEST
    }

    [Fact]
    public void Options_bind_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:ReminderTimeOfDay"] = "07:30",
            ["Notifications:ReminderPollInterval"] = "00:05:00",
            ["Notifications:PollInterval"] = "00:01:00",
            ["Notifications:MaxAttempts"] = "5",
            ["Notifications:IncludePhoneNumberInMail"] = "false",
        }).Build();
        var o = config.GetSection(NotificationOptions.SectionName).Get<NotificationOptions>()!;
        Assert.Equal(new TimeOnly(7, 30), o.ReminderTimeOfDay);
        Assert.Equal(TimeSpan.FromMinutes(5), o.ReminderPollInterval);
        Assert.Equal(TimeSpan.FromMinutes(1), o.PollInterval);
        Assert.Equal(5, o.MaxAttempts);
        Assert.False(o.IncludePhoneNumberInMail);
        Assert.True(new NotificationOptions().IncludePhoneNumberInMail); // sponsor decision D5: default true
        Assert.Equal(TimeSpan.FromMinutes(15), new NotificationOptions().ReminderPollInterval);
    }
}
