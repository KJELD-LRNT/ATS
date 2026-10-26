using AssetTracker.Core.Admin;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Directory;
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
        services.AddScoped<AssetService>();
        services.AddScoped<PoolService>();

        // Notifications (WP2.3). Options are bound from "Notifications" by the infrastructure layer.
        services.AddOptions<NotificationOptions>();
        services.AddScoped<NotificationService>();
        services.AddScoped<OutboxSender>();
        services.AddScoped<OutboxMonitor>();
        services.AddScoped<OverdueReminderService>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<DirectorySyncService>();
        services.AddScoped<AdminService>();
        return services;
    }
}
