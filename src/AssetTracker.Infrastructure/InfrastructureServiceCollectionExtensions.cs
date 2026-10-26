using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Data;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Ops;
using AssetTracker.Infrastructure.Mail;
using AssetTracker.Core.Directory;
using AssetTracker.Infrastructure.Directory;
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

        services.Configure<NotificationOptions>(config.GetSection(NotificationOptions.SectionName));
        services.Configure<MailOptions>(config.GetSection(MailOptions.SectionName));
        services.AddSingleton<IMailRelay, SmtpMailRelay>();
        services.Configure<DirectorySyncOptions>(config.GetSection(DirectorySyncOptions.SectionName));
        services.Configure<OpsOptions>(config.GetSection(OpsOptions.SectionName));
        services.Configure<ActiveDirectoryOptions>(config.GetSection(ActiveDirectoryOptions.SectionName));
        if (OperatingSystem.IsWindows())
            services.AddScoped<IDirectorySource, ActiveDirectorySource>();
        else
            services.AddScoped<IDirectorySource, UnsupportedDirectorySource>();
        return services;
    }
}
