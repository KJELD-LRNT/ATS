using AssetTracker.Core.Admin;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Ops;
using AssetTracker.Core.Privacy;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Reporting;
using AssetTracker.Core.Security;
using AssetTracker.Core.Settings;
using AssetTracker.Core.Time;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers scoped application services. Feature work packages add their services here.</summary>
    public static IServiceCollection AddAssetTrackerCore(this IServiceCollection services)
    {
        services.AddScoped<AccessScope>();
        // Per-site calendar (WP4.2). ClockOptions are bound from "Clock" by the infrastructure layer.
        services.AddOptions<ClockOptions>();
        services.AddScoped<SiteCalendar>();
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
        // Database-backed settings (WP4.1). ISettingsReloader/ISecretProtector come from the infrastructure layer.
        services.AddScoped<SettingsService>();

        // Lending (WP2.1/WP2.2). Staleness uses DirectorySyncOptions.StaleAfter via DirectorySyncService.
        services.AddScoped<IDirectoryFreshness, SyncRunDirectoryFreshness>();
        services.AddScoped<LendingService>();
        services.AddScoped<CorrectionService>();
        services.AddScoped<LoanHistoryService>();
        // Reports and CSV exports (WP3.1).
        services.AddScoped<ReportService>();

        // Operations and privacy (WP3.2). OpsOptions are bound from "Ops" by the infrastructure layer.
        services.AddOptions<OpsOptions>();
        services.AddScoped<OpsStatusService>();
        services.AddScoped<PrivacyService>();
        return services;
    }
}
