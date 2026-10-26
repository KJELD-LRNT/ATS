using AssetTracker.Core.Auditing;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers scoped application services. Feature work packages add their services here.</summary>
    public static IServiceCollection AddAssetTrackerCore(this IServiceCollection services)
    {
        services.AddScoped<AccessScope>();
        services.AddScoped<AuditWriter>();

        // Notifications (WP2.3). Options are bound from "Notifications" by the infrastructure layer.
        services.AddOptions<NotificationOptions>();
        services.AddScoped<NotificationService>();
        services.AddScoped<OutboxSender>();
        services.AddScoped<OutboxMonitor>();
        services.AddScoped<OverdueReminderService>();
        return services;
    }
}
