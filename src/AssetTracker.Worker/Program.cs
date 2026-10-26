using AssetTracker.Core;
using AssetTracker.Core.Security;
using AssetTracker.Infrastructure;
using AssetTracker.Infrastructure.Configuration;
using AssetTracker.Infrastructure.Security;
using AssetTracker.Infrastructure.Jobs;
using AssetTracker.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "AssetTracker Worker");
// Settings saved on Admin → Settings (AppSettings table) override the appsettings files. Keep this the last source.
builder.Configuration.AddDatabaseSettings();

builder.Services.AddAssetTrackerCore();
builder.Services.AddAssetTrackerInfrastructure(builder.Configuration);
// Same key ring as the web app, to decrypt the SMTP password saved there.
builder.Services.AddAssetTrackerDataProtection(builder.Configuration, builder.Environment);
builder.Services.AddScoped<ICurrentUser, SystemCurrentUser>();

// Background jobs (outbox sender, per-site overdue reminders, AD sync, heartbeat; Infrastructure/Jobs).
builder.Services.AddAssetTrackerBackgroundJobs(builder.Configuration);

var host = builder.Build();
// First load of database settings before the jobs start (tolerates a database that is not migrated yet).
await host.Services.LoadDatabaseSettingsAsync();
host.Run();
