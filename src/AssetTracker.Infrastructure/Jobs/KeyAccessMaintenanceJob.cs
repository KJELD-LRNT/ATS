using AssetTracker.Core.Keys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssetTracker.Infrastructure.Jobs;

/// <summary>Independent of IT overdue reminders: expiry requests run even if other jobs fail.</summary>
public class KeyAccessMaintenanceJob(IServiceScopeFactory scopes, ILogger<KeyAccessMaintenanceJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<KeyAccessMaintenance>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Key access maintenance failed; retrying on the next tick"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
