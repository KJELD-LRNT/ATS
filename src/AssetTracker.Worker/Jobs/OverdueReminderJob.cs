using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Notifications;
using AssetTracker.Infrastructure.Time;
using Microsoft.Extensions.Options;

namespace AssetTracker.Worker.Jobs;

/// <summary>
/// Queues daily overdue reminders at <see cref="NotificationOptions.ReminderTimeOfDay"/> in the client timezone
/// (Clock:TimeZoneId). On start-up it catches up if today's reminder time has passed without a completed run.
/// Reminder dedupe keys make an extra run harmless.
/// </summary>
public class OverdueReminderJob(IServiceScopeFactory scopes, IClock clock, IOptions<ClockOptions> clockOptions,
    IOptionsMonitor<NotificationOptions> options, ILogger<OverdueReminderJob> logger) : BackgroundService
{
    /// <summary>Upper bound on a single sleep, so clock changes and config reloads are picked up.</summary>
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(clockOptions.Value.TimeZoneId);
        while (!stoppingToken.IsCancellationRequested)
        {
            var timeOfDay = options.CurrentValue.ReminderTimeOfDay;
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<OverdueReminderService>();
                var ran = await service.HasRunForAsync(clock.LocalToday, stoppingToken);
                if (ReminderSchedule.ShouldRunNow(clock.UtcNow, tz, timeOfDay, ran))
                    await service.QueueRemindersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Overdue reminder run failed; it will be retried");
            }

            var wait = ReminderSchedule.NextRunUtc(clock.UtcNow, tz, timeOfDay) - clock.UtcNow;
            if (wait > MaxSleep) wait = MaxSleep;
            if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
