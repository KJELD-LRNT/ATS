using AssetTracker.Core;
using AssetTracker.Core.Security;
using AssetTracker.Infrastructure;
using AssetTracker.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "AssetTracker Worker");

builder.Services.AddAssetTrackerCore();
builder.Services.AddAssetTrackerInfrastructure(builder.Configuration);
builder.Services.AddScoped<ICurrentUser, SystemCurrentUser>();

// Background jobs (AD sync, outbox sender, daily overdue reminders) are registered by their work packages.

var host = builder.Build();
host.Run();
