using AssetTracker.Core.Settings;
using AssetTracker.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Settings;

/// <summary>
/// An <see cref="AppFactory"/> whose "configuration file" sets a few values the tests override from the database.
/// Each test class gets its own instance (settings are process-wide configuration).
/// </summary>
public class SettingsFactory : AppFactory
{
    public const string FileFromName = "File Sender";
    public const string FileHost = "relay-from-file.example.test";
    public const string FileFromAddress = "from-file@example.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mail:FromName"] = FileFromName,
            ["Mail:Host"] = FileHost,
            ["Mail:FromAddress"] = FileFromAddress,
            ["ActiveDirectory:PageSize"] = "300",
        }));
    }

    public Task<IReadOnlyList<string>> SaveAsync(string section, params (string Key, string? Value)[] values) =>
        SaveAsync(section, false, values);

    public Task<IReadOnlyList<string>> SaveAsync(string section, bool clearPassword, params (string Key, string? Value)[] values) =>
        RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<SettingsService>().SaveSectionAsync(
            section, values.ToDictionary(v => v.Key, v => v.Value), clearPassword));

    public Task<SettingsOverview> OverviewAsync() =>
        RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<SettingsService>().GetAsync());

    public async Task<SettingView> ViewAsync(string key) =>
        (await OverviewAsync()).Settings.Single(s => s.Definition.Key == key);
}
