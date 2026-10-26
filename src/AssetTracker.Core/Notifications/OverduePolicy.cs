namespace AssetTracker.Core.Notifications;

/// <summary>
/// The single place that defines when a dated, open assignment is overdue.
/// Current rule: overdue from the client-local calendar day <b>after</b> the due date (an item due on the 9th
/// is on time all of the 9th and overdue from 00:00 local on the 10th). "Today" is <c>IClock.LocalToday</c>
/// in the configured client timezone. Stage 0 confirms the actual cutoff; change only this class if it differs.
/// </summary>
public static class OverduePolicy
{
    /// <summary>The latest due date that counts as overdue on <paramref name="localToday"/>.</summary>
    public static DateOnly LatestOverdueDueDate(DateOnly localToday) => localToday.AddDays(-1);

    public static bool IsOverdue(DateOnly? dueDate, DateOnly localToday) =>
        dueDate is { } d && d <= LatestOverdueDueDate(localToday);

    public static int DaysOverdue(DateOnly dueDate, DateOnly localToday) =>
        Math.Max(0, localToday.DayNumber - dueDate.DayNumber);
}
