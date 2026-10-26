using AssetTracker.Core.Notifications;
using Microsoft.Extensions.Options;

namespace AssetTracker.Worker.Jobs;

/// <summary>
/// Polls the mail outbox every <see cref="NotificationOptions.PollInterval"/> and drains due rows in batches.
/// Rows live in the database, so a restart or relay outage never loses them.
/// </summary>
public class OutboxSenderJob(IServiceScopeFactory scopes, IOptionsMonitor<NotificationOptions> options,
    ILogger<OutboxSenderJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var o = options.CurrentValue;
            try
            {
                OutboxBatchResult result;
                do
                {
                    using var scope = scopes.CreateScope();
                    var sender = scope.ServiceProvider.GetRequiredService<OutboxSender>();
                    result = await sender.ProcessDueAsync(Math.Max(1, o.BatchSize), stoppingToken);
                    // Keep draining while the batch was full and the relay is accepting mail.
                } while (result.Claimed >= o.BatchSize && result.Sent > 0 && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox sender pass failed; retrying after the poll interval");
            }

            try { await Task.Delay(o.PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
