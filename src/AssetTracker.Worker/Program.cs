using AssetTracker.Core;
using AssetTracker.Core.Security;
using AssetTracker.Infrastructure;
using AssetTracker.Worker;
using AssetTracker.Worker.Jobs;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "AssetTracker Worker");

builder.Services.AddAssetTrackerCore();
builder.Services.AddAssetTrackerInfrastructure(builder.Configuration);
builder.Services.AddScoped<ICurrentUser, SystemCurrentUser>();

// Background jobs (AD sync, outbox sender, daily overdue reminders) are registered by their work packages.
builder.Services.AddHostedService<OutboxSenderJob>();
builder.Services.AddHostedService<OverdueReminderJob>();
builder.Services.AddHostedService<AssetTracker.Worker.Jobs.DirectorySyncJob>();
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));
builder.Services.AddHostedService<WorkerHeartbeatJob>();

var host = builder.Build();
host.Run();
