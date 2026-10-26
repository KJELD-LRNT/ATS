using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Data;
using AssetTracker.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers SQL Server persistence and production adapters.
    /// External boundaries (directory, mail relay) are registered here and replaced in tests.
    /// </summary>
    public static IServiceCollection AddAssetTrackerInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(
            config.GetConnectionString("AssetTracker"),
            sql => sql.MigrationsAssembly(typeof(InfrastructureServiceCollectionExtensions).Assembly.FullName)));

        services.Configure<ClockOptions>(config.GetSection("Clock"));
        services.AddSingleton<IClock, SystemClock>();
        return services;
    }
}
