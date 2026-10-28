using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Infrastructure.Jobs;

public static class BackgroundJobsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the background jobs (mail outbox sender, per-site overdue reminders, scheduled directory sync and
    /// the worker heartbeat). Hosted by the Windows worker service in production, and by the web process in local
    /// demo mode. The host must register an <c>ICurrentUser</c> that works without an HTTP request.
    /// </summary>
    public static IServiceCollection AddAssetTrackerBackgroundJobs(this IServiceCollection services, IConfiguration config)
    {
        services.AddHostedService<OutboxSenderJob>();
        services.AddHostedService<OverdueReminderJob>();
        services.AddHostedService<KeyAccessMaintenanceJob>();
        services.AddHostedService<DirectorySyncJob>();
        services.Configure<WorkerOptions>(config.GetSection(WorkerOptions.SectionName));
        services.AddHostedService<WorkerHeartbeatJob>();
        return services;
    }
}
