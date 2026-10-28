using System.Text.Json;
using AssetTracker.Core;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Data;
using AssetTracker.Core.Security;
using AssetTracker.Infrastructure.SuperAdminSetup;
using AssetTracker.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssetTracker.Tests.Security;

/// <summary>
/// The worker's <c>superadmin</c> setup command (<c>hash</c>, <c>apply &lt;file&gt;</c>) with the same registrations as
/// the worker (<c>AddAssetTrackerCore</c> + <c>AddSuperAdminSetupCommand</c>), a SQLite database and a fake file owner.
/// </summary>
public sealed class SuperAdminSetupCommandTests : IDisposable
{
    private const string Requester = @"CORP\it.admin";
    private const string Password = "a long and unguessable passphrase";

    private sealed class FakeOwner : IRequestFileOwner
    {
        public string GetOwner(string path) => Requester;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ats_setup_" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _sp;
    private readonly FakeClock _clock = new();

    public SuperAdminSetupCommandTests()
    {
        System.IO.Directory.CreateDirectory(_dir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAssetTrackerCore();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_dir, "db.sqlite")}"));
        services.AddSingleton<IClock>(_clock);
        services.AddSuperAdminSetupCommand();
        services.Replace(ServiceDescriptor.Singleton<IRequestFileOwner, FakeOwner>());
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SuperAdminCommand Command => _sp.GetRequiredService<SuperAdminCommand>();

    private async Task<(int Code, string Out, string Error)> RunAsync(string input, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await Command.RunAsync(args, new CommandConsole(new StringReader(input), true, output, error));
        return (code, output.ToString().Trim(), error.ToString().Trim());
    }

    private async Task<string> HashAsync(string password)
    {
        var (code, hash, _) = await RunAsync(password + "\n", "hash");
        Assert.Equal(SuperAdminCommand.Ok, code);
        return hash;
    }

    private async Task<(int Code, JsonElement Result, string RequestPath)> ApplyAsync(object request)
    {
        var path = Path.Combine(_dir, $"request-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request));
        var (code, _, _) = await RunAsync("", "apply", path);
        var resultPath = SuperAdminCommand.ResultPathFor(path);
        Assert.True(File.Exists(resultPath), "the result file is written next to the request");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
        return (code, doc.RootElement.Clone(), path);
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = _sp.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task Hash_makes_a_verifiable_hash_and_needs_no_database()
    {
        var hash = await HashAsync(Password);
        Assert.True(SuperAdmin.IsValidHash(hash));
        Assert.Equal(PasswordVerificationResult.Success, SuperAdmin.VerifyPassword(hash, Password));
        Assert.Equal(PasswordVerificationResult.Failed, SuperAdmin.VerifyPassword(hash, Password + "x"));
        Assert.False(await DbAsync(db => db.LocalAccounts.AnyAsync()));
    }

    [Theory]
    [InlineData("fifteen chars!!")]
    [InlineData("")]
    [InlineData(SuperAdmin.DemoPassword)]
    [InlineData("DEMO-SUPERADMIN-2026")]
    public async Task Hash_refuses_short_passwords_and_the_demo_password(string password)
    {
        var (code, output, error) = await RunAsync(password + "\n", "hash");
        Assert.Equal(SuperAdminCommand.Usage, code);
        Assert.Equal("", output);
        Assert.NotEqual("", error);
    }

    [Fact]
    public async Task Wrong_usage_prints_help()
    {
        Assert.Equal(SuperAdminCommand.Usage, (await RunAsync("", "frobnicate")).Code);
        Assert.Equal(SuperAdminCommand.Usage, (await RunAsync("", "apply")).Code);
    }

    [Theory]
    [InlineData("set-password")]
    [InlineData("generate-password")]
    public async Task Set_and_generate_store_the_hash_enable_change_the_stamp_and_audit_the_file_owner(string action)
    {
        var hash = await HashAsync(Password);
        // A requester written inside the file is ignored: only the file's owner counts.
        var (code, result, request) = await ApplyAsync(new { action, hash, requester = @"EVIL\someone" });
        Assert.Equal(SuperAdminCommand.Ok, code);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(Requester, result.GetProperty("requester").GetString());
        Assert.True(result.GetProperty("account").GetProperty("enabled").GetBoolean());
        Assert.False(File.Exists(request), "the request file is deleted");

        var account = await DbAsync(db => db.LocalAccounts.AsNoTracking().SingleAsync());
        Assert.Equal((SuperAdmin.UserName, hash, true), (account.UserName, account.PasswordHash, account.IsEnabled));
        var firstStamp = account.SecurityStamp;
        Assert.NotEqual("", firstStamp);

        var audit = await DbAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == "superadmin.password-set"));
        Assert.Equal(Requester, audit.Actor);
        Assert.Contains(action, audit.DetailsJson);
        Assert.DoesNotContain(hash, audit.DetailsJson);

        // Again: a new stamp (ends sessions), counter and lock cleared.
        await DbAsync(db => db.LocalAccounts.ExecuteUpdateAsync(s => s
            .SetProperty(a => a.FailedCount, 5).SetProperty(a => a.LockedUntil, _clock.UtcNow.AddMinutes(10))));
        Assert.Equal(SuperAdminCommand.Ok, (await ApplyAsync(new { action, hash = await HashAsync(Password + "2") })).Code);
        account = await DbAsync(db => db.LocalAccounts.AsNoTracking().SingleAsync());
        Assert.NotEqual(firstStamp, account.SecurityStamp);
        Assert.Equal((0, (DateTimeOffset?)null), (account.FailedCount, account.LockedUntil));
    }

    [Fact]
    public async Task Unlock_disable_and_status()
    {
        // Before any password: unlock is refused, status says it is not set up.
        var (code, result, _) = await ApplyAsync(new { action = "unlock" });
        Assert.Equal(SuperAdminCommand.Failed, code);
        Assert.False(result.GetProperty("ok").GetBoolean());
        (code, result, _) = await ApplyAsync(new { action = "status" });
        Assert.Equal(SuperAdminCommand.Ok, code);
        Assert.Contains("not been set up", result.GetProperty("message").GetString());

        await ApplyAsync(new { action = "set-password", hash = await HashAsync(Password) });
        var until = _clock.UtcNow.AddMinutes(10);
        await DbAsync(db => db.LocalAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.FailedCount, 5).SetProperty(a => a.LockedUntil, until)));
        (_, result, _) = await ApplyAsync(new { action = "status" });
        Assert.True(result.GetProperty("account").GetProperty("locked").GetBoolean());
        Assert.Contains("locked until", result.GetProperty("message").GetString());

        (code, result, _) = await ApplyAsync(new { action = "unlock" });
        Assert.Equal(SuperAdminCommand.Ok, code);
        var account = await DbAsync(db => db.LocalAccounts.AsNoTracking().SingleAsync());
        Assert.Equal((0, (DateTimeOffset?)null, true), (account.FailedCount, account.LockedUntil, account.IsEnabled));

        var stamp = account.SecurityStamp;
        (code, result, _) = await ApplyAsync(new { action = "disable" });
        Assert.Equal(SuperAdminCommand.Ok, code);
        Assert.False(result.GetProperty("account").GetProperty("enabled").GetBoolean());
        account = await DbAsync(db => db.LocalAccounts.AsNoTracking().SingleAsync());
        Assert.False(account.IsEnabled);
        Assert.NotEqual(stamp, account.SecurityStamp);

        var actions = await DbAsync(db => db.AuditEvents.AsNoTracking().OrderBy(a => a.Id).Select(a => new { a.Action, a.Actor }).ToListAsync());
        Assert.Equal(["superadmin.status", "superadmin.password-set", "superadmin.status", "superadmin.unlock", "superadmin.disable"],
            actions.Select(a => a.Action));
        Assert.All(actions, a => Assert.Equal(Requester, a.Actor));
    }

    [Fact]
    public async Task An_invalid_request_changes_nothing_and_is_still_deleted()
    {
        foreach (var request in new object[]
                 {
                     new { action = "set-password", hash = "not-a-hash" },
                     new { action = "set-password" },
                     new { action = "make-me-admin" },
                 })
        {
            var (code, result, path) = await ApplyAsync(request);
            Assert.Equal(SuperAdminCommand.Failed, code);
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.False(File.Exists(path));
        }
        var broken = Path.Combine(_dir, "broken.json");
        await File.WriteAllTextAsync(broken, "{ not json");
        Assert.Equal(SuperAdminCommand.Failed, (await RunAsync("", "apply", broken)).Code);
        Assert.False(File.Exists(broken));
        Assert.False(await DbAsync(db => db.LocalAccounts.AnyAsync()));
    }

    [Fact]
    public void The_non_windows_owner_fallback_reads_the_files_owner()
    {
        if (OperatingSystem.IsWindows()) return; // production path: WindowsRequestFileOwner (ACL owner), not testable here
        var path = Path.Combine(_dir, "owned.json");
        File.WriteAllText(path, "{}");
        Assert.Equal(Environment.UserName, new UnixRequestFileOwner().GetOwner(path));
    }
}
