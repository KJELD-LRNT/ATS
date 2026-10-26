using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Data;
using AssetTracker.Infrastructure.Jobs;
using AssetTracker.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssetTracker.Infrastructure.Demo;

public static class DemoServiceCollectionExtensions
{
    /// <summary>
    /// Adds the in-memory demo configuration (role group SIDs, short job intervals, default timezone). Call before
    /// <c>AddDatabaseSettings()</c> so settings saved in the app still win.
    /// </summary>
    public static IConfigurationBuilder AddDemoDefaults(this IConfigurationBuilder builder) =>
        builder.AddInMemoryCollection(DemoDefaults.Configuration());

    /// <summary>
    /// Demo mode: replaces the production adapters registered by <c>AddAssetTrackerInfrastructure</c> and
    /// <c>AddAssetTrackerDataProtection</c> (call this after both) with SQLite at <c>&lt;DataDirectory&gt;/demo.db</c>,
    /// the fictional directory, the pickup-folder relay, the time-travel clock and a key ring in
    /// <c>&lt;DataDirectory&gt;/keys</c>; optionally hosts the background jobs in this process.
    /// </summary>
    public static IServiceCollection AddAssetTrackerDemo(this IServiceCollection services, DemoOptions demo,
        IConfiguration config)
    {
        System.IO.Directory.CreateDirectory(demo.DataDirectory);
        services.AddSingleton(demo);

        services.RemoveAll<DbContextOptions<AppDbContext>>();
        services.RemoveAll<AppDbContext>();
        services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
        var connection = $"Data Source={demo.DatabasePath};Default Timeout=30";
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));

        services.RemoveAll<IClock>();
        services.AddSingleton<DemoClock>();
        services.AddSingleton<IClock>(sp => sp.GetRequiredService<DemoClock>());

        services.RemoveAll<IDirectorySource>();
        services.AddScoped<IDirectorySource, DemoDirectorySource>();

        services.RemoveAll<IMailRelay>();
        services.AddSingleton<DemoMailbox>();
        services.AddSingleton<IMailRelay, PickupDirectoryMailRelay>();

        // Plain file key ring (no DPAPI), so saving the SMTP password on the settings page works on any OS.
        services.AddDataProtection().SetApplicationName(DataProtectionSetup.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(demo.KeysDirectory));

        services.AddSingleton<DemoSeeder>();
        services.AddSingleton<DemoDataManager>();

        if (demo.RunBackgroundJobs)
            services.AddAssetTrackerBackgroundJobs(config);
        return services;
    }
}
