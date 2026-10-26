using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Domain;
using AssetTracker.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Directory;

/// <summary>
/// An <see cref="AppFactory"/> with extra configuration. Sync tests create one per test, because a full sync
/// deactivates every person not in the directory result and would interfere with other tests sharing a database.
/// </summary>
public class SyncFactory(Dictionary<string, string?>? config = null) : AppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (config is not null)
            builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(config));
    }

    public Task<SyncRun> SyncAsync(string triggeredBy = "test") =>
        RunAsAsync(TestUser.Nobody, sp => sp.GetRequiredService<DirectorySyncService>().RunAsync(triggeredBy));

    public Task<DirectorySyncStatus> StatusAsync() =>
        RunAsAsync(TestUser.Nobody, sp => sp.GetRequiredService<DirectorySyncService>().GetStatusAsync());
}

/// <summary>Fictional directory entries.</summary>
public static class Entries
{
    public static DirectoryEntry Person(string id, string username, string? site = "Head Office", bool enabled = true,
        string? sid = null, string? title = "Analyst") =>
        new(id, sid, username, $"Test {username}", title, $"{username}@example.test", site, enabled);

    /// <summary>n active people with ids "id-0".."id-(n-1)" at the given site value.</summary>
    public static List<DirectoryEntry> Many(int n, string? site = "Head Office") =>
        Enumerable.Range(0, n).Select(i => Person($"id-{i}", $"user{i}", site)).ToList();
}
