using AssetTracker.Core.Auditing;
using AssetTracker.Core.Inventory;
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
        return services;
    }
}
