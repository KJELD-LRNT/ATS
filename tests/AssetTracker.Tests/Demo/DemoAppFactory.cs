using AssetTracker.Core.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Demo;

/// <summary>
/// The real app in local demo mode: Development environment, <c>Demo:Enabled=true</c>, a temporary data folder,
/// background jobs off (tests drive time and jobs themselves). Authentication is the demo scheme, so clients need
/// no headers; the role switcher cookie selects the identity.
/// </summary>
public class DemoAppFactory : WebApplicationFactory<Program>
{
    public string DataDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "ats_demo_test_" + Guid.NewGuid().ToString("N"));

    /// <summary>Leave the data folder on dispose (a test starts a second app on the same folder).</summary>
    public bool KeepDataDirectory { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Host settings are visible to Program.cs before the builder is built (where demo mode is decided).
        builder.UseSetting("Demo:Enabled", "true");
        builder.UseSetting("Demo:DataDirectory", DataDirectory);
        builder.UseSetting("Demo:RunBackgroundJobs", "false");
    }

    public HttpClient CreateDemoClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        if (KeepDataDirectory) return;
        try { System.IO.Directory.Delete(DataDirectory, recursive: true); } catch { /* best effort */ }
    }
}
