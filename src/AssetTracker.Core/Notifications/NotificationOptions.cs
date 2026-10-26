namespace AssetTracker.Core.Notifications;

/// <summary>Bound from the "Notifications" configuration section.</summary>
public class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// Whether a phone/phone-number asset's normalized number may appear in mail bodies. Default false until
    /// Stage 0 confirms what reminder/notification mail may contain (mvp-plan §7.4).
    /// </summary>
    public bool IncludePhoneNumberInMail { get; set; }

    /// <summary>Send attempts per row before it becomes <see cref="Domain.NotificationStatus.Failed"/>.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Retry backoff: first delay, doubled per attempt and capped at <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a sender's claim on a row lasts. If a worker dies mid-send the row becomes due again after this.
    /// Must comfortably exceed the SMTP timeout.
    /// </summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Worker: how often the outbox is polled.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Worker: rows claimed per pass.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Site-local time of day from which a site's daily overdue reminders are queued (each site in its own timezone,
    /// see <c>SiteCalendar</c>). A worker that starts later in the day still queues that day's reminders on its next
    /// tick. Stage 0 confirms the preferred time.
    /// </summary>
    public TimeOnly ReminderTimeOfDay { get; set; } = new(8, 0);

    /// <summary>
    /// Worker: how often the overdue-reminder job ticks. Each tick queues reminders for every site whose local time
    /// has reached <see cref="ReminderTimeOfDay"/>; dedupe keys make repeated ticks harmless.
    /// </summary>
    public TimeSpan ReminderPollInterval { get; set; } = TimeSpan.FromMinutes(15);
}
