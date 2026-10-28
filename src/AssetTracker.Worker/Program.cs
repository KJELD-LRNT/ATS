using AssetTracker.Core;
using AssetTracker.Core.Security;
using AssetTracker.Infrastructure;
using AssetTracker.Infrastructure.Configuration;
using AssetTracker.Infrastructure.Security;
using AssetTracker.Infrastructure.Jobs;
using AssetTracker.Infrastructure.SuperAdminSetup;
using AssetTracker.Worker;

// "AssetTracker.Worker.exe superadmin hash|apply <file>": the super admin setup command (deploy/Set-SuperAdmin.ps1).
// It runs as a one-off process (apply: a scheduled task under the gMSA) and never starts the background jobs.
var superAdminCommand = args.Length > 0 && string.Equals(args[0], "superadmin", StringComparison.OrdinalIgnoreCase);

var builder = superAdminCommand
    // Started by Task Scheduler (or from any folder): read appsettings.Production.json next to the executable.
    // The command's own arguments are not configuration.
    ? Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory })
    : Host.CreateApplicationBuilder(args);
if (superAdminCommand)
{
    // Standard output carries only the command's result (the hash); logs go to standard error.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
}
else
{
    builder.Services.AddWindowsService(o => o.ServiceName = "AssetTracker Worker");
}
// Settings saved on Admin → Settings (AppSettings table) override the appsettings files. Keep this the last source.
builder.Configuration.AddDatabaseSettings();

builder.Services.AddAssetTrackerCore();
builder.Services.AddAssetTrackerInfrastructure(builder.Configuration);
if (superAdminCommand)
{
    // Its own ICurrentUser: the Windows account that owns the request file (audit actor), not system:worker.
    builder.Services.AddSuperAdminSetupCommand();
}
else
{
    // Same key ring as the web app, to decrypt the SMTP password saved there.
    builder.Services.AddAssetTrackerDataProtection(builder.Configuration, builder.Environment);
    builder.Services.AddScoped<ICurrentUser, SystemCurrentUser>();

    // Background jobs (outbox sender, per-site overdue reminders, AD sync, heartbeat; Infrastructure/Jobs).
    builder.Services.AddAssetTrackerBackgroundJobs(builder.Configuration);
}

var host = builder.Build();
// The setup command: run it and exit. host.Run() is never called, so no hosted service (job) starts.
if (superAdminCommand)
    return await host.Services.GetRequiredService<SuperAdminCommand>().RunAsync(args[1..], CommandConsole.System());

// First load of database settings before the jobs start (tolerates a database that is not migrated yet).
await host.Services.LoadDatabaseSettingsAsync();
host.Run();
return 0;
