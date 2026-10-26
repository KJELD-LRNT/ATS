using AssetTracker.Core.Admin;
using AssetTracker.Core.Auditing;
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
        services.AddScoped<AuditQueryService>();
        services.AddScoped<DirectorySyncService>();
        services.AddScoped<AdminService>();
        return services;
    }
}
