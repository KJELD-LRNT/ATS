using AssetTracker.Core.Notifications;
using Microsoft.Extensions.Options;

namespace AssetTracker.Worker.Jobs;

/// <summary>
/// Ticks every <see cref="NotificationOptions.ReminderPollInterval"/> (default 15 minutes) and calls
/// <see cref="OverdueReminderService.QueueRemindersAsync"/>, which queues reminders for every site whose local time
/// has reached <see cref="NotificationOptions.ReminderTimeOfDay"/> in that site's timezone. The per-site-day dedupe
/// key guarantees one reminder per loan, site-local day and recipient however often it ticks, so there is no
/// separate "has run today" or start-up catch-up logic: the first tick after a restart simply queues what is due.
/// </summary>
public class OverdueReminderJob(IServiceScopeFactory scopes, IOptionsMonitor<NotificationOptions> options,
    ILogger<OverdueReminderJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<OverdueReminderService>().QueueRemindersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Overdue reminder tick failed; it will be retried on the next tick");
            }

            var wait = options.CurrentValue.ReminderPollInterval;
            if (wait < TimeSpan.FromMinutes(1)) wait = TimeSpan.FromMinutes(1);
            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
