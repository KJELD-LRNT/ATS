using AssetTracker.Core;
using AssetTracker.Core.Security;
using AssetTracker.Infrastructure;
using AssetTracker.Infrastructure.Configuration;
using AssetTracker.Infrastructure.Security;
using AssetTracker.Worker;
using AssetTracker.Worker.Jobs;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "AssetTracker Worker");
// Settings saved on Admin → Settings (AppSettings table) override the appsettings files. Keep this the last source.
builder.Configuration.AddDatabaseSettings();

builder.Services.AddAssetTrackerCore();
builder.Services.AddAssetTrackerInfrastructure(builder.Configuration);
// Same key ring as the web app, to decrypt the SMTP password saved there.
builder.Services.AddAssetTrackerDataProtection(builder.Configuration, builder.Environment);
builder.Services.AddScoped<ICurrentUser, SystemCurrentUser>();

// Background jobs (AD sync, outbox sender, daily overdue reminders) are registered by their work packages.
builder.Services.AddHostedService<OutboxSenderJob>();
builder.Services.AddHostedService<OverdueReminderJob>();
builder.Services.AddHostedService<AssetTracker.Worker.Jobs.DirectorySyncJob>();
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));
builder.Services.AddHostedService<WorkerHeartbeatJob>();

var host = builder.Build();
// First load of database settings before the jobs start (tolerates a database that is not migrated yet).
await host.Services.LoadDatabaseSettingsAsync();
host.Run();
