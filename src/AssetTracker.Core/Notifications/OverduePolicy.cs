namespace AssetTracker.Core.Notifications;

/// <summary>
/// The single place that defines when a dated, open assignment is overdue.
/// <para>
/// Rule (sponsor decision D4, <c>docs/decisions.md</c>): an assignment is overdue <b>from the start of its due date</b>
/// in the local calendar of the loan's site: overdue when <c>siteToday &gt;= DueDate</c>. An item due on the 9th is
/// overdue (and reminded) from 00:00 site-local on the 9th. "Today" is the loan's site's local day from
/// <see cref="Time.SiteCalendar"/> (tagged loan: the asset's current site; pool loan: the pool's site).
/// </para>
/// <para>
/// <see cref="DaysOverdue"/> counts whole days <i>past</i> the due date: 0 on the due date itself ("due today"),
/// 1 the day after, and so on. Mail and reports show 0 as "due today".
/// </para>
/// </summary>
public static class OverduePolicy
{
    /// <summary>The latest due date that counts as overdue on <paramref name="localToday"/> (today itself, D4).</summary>
    public static DateOnly LatestOverdueDueDate(DateOnly localToday) => localToday;

    public static bool IsOverdue(DateOnly? dueDate, DateOnly localToday) =>
        dueDate is { } d && d <= LatestOverdueDueDate(localToday);

    /// <summary>Whole days past the due date: 0 on the due date, never negative.</summary>
    public static int DaysOverdue(DateOnly dueDate, DateOnly localToday) =>
        Math.Max(0, localToday.DayNumber - dueDate.DayNumber);
}
