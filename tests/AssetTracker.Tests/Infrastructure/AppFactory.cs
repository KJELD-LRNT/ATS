using System.Net.Http.Headers;
using System.Security.Claims;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AssetTracker.Tests.Infrastructure;

/// <summary>
/// Browser-to-app-to-database test host. Uses a real relational database (a temp SQLite file by default,
/// or SQL Server when ATS_TEST_SQLSERVER is set), simulated Windows principals, and fake AD/relay adapters.
/// Each factory instance gets its own empty database.
/// </summary>
public class AppFactory : WebApplicationFactory<Program>
{
    public const string SqlServerEnvVar = "ATS_TEST_SQLSERVER";

    private readonly string _dbName = "ats_test_" + Guid.NewGuid().ToString("N");
    private string? _sqlitePath;

    public FakeClock Clock { get; } = new();
    public FakeDirectorySource Directory { get; } = new();
    public FakeMailRelay Mail { get; } = new();

    public static bool UsingSqlServer => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SqlServerEnvVar));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Clock:TimeZoneId"] = "UTC",
            ["Roles:ItGroupSids:0"] = TestGroups.It,
            ["Roles:ManagerGroupSids:0"] = TestGroups.Manager,
            ["Roles:AdminGroupSids:0"] = TestGroups.Admin,
        }));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(ConfigureDb);

            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
            services.RemoveAll<IDirectorySource>();
            services.AddSingleton<IDirectorySource>(Directory);
            services.RemoveAll<IMailRelay>();
            services.AddSingleton<IMailRelay>(Mail);

            services.AddAuthentication(o =>
            {
                o.DefaultScheme = TestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    private void ConfigureDb(DbContextOptionsBuilder o)
    {
        var sql = Environment.GetEnvironmentVariable(SqlServerEnvVar);
        if (!string.IsNullOrEmpty(sql))
        {
            var cs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sql) { InitialCatalog = _dbName };
            o.UseSqlServer(cs.ConnectionString);
        }
        else
        {
            _sqlitePath ??= Path.Combine(Path.GetTempPath(), _dbName + ".db");
            o.UseSqlite($"Data Source={_sqlitePath};Default Timeout=30");
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            // The migration inserts the default asset types and fields; a database built from the model gets them here.
            AssetTracker.Core.Catalog.CatalogDefaults.EnsureAsync(db).GetAwaiter().GetResult();
        }
        // Program.cs's first settings load ran before the schema existed (tolerated); load again now.
        ReloadSettingsAsync(host.Services).GetAwaiter().GetResult();
        return host;
    }

    /// <summary>Applies database settings rows to configuration now (as the Settings page does after a save).</summary>
    public Task ReloadSettingsAsync() => ReloadSettingsAsync(Services);

    private static Task ReloadSettingsAsync(IServiceProvider services) =>
        services.GetRequiredService<AssetTracker.Core.Settings.ISettingsReloader>().ReloadAsync();

    /// <summary>HTTP client authenticated as the given simulated Windows user. Does not follow redirects.</summary>
    public HttpClient CreateClientAs(TestUser user)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user.Name);
        client.DefaultRequestHeaders.Add(TestAuthHandler.SidHeader, user.Sid);
        client.DefaultRequestHeaders.Add(TestAuthHandler.GroupsHeader, string.Join(',', user.GroupSids));
        return client;
    }

    /// <summary>
    /// Runs service-level code in a DI scope as the given user, going through the same claims
    /// transformation as HTTP requests.
    /// </summary>
    public async Task<T> RunAsAsync<T>(TestUser user, Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        var sp = scope.ServiceProvider;
        var principal = TestAuthHandler.BuildPrincipal(user.Name, user.Sid, user.GroupSids);
        principal = await sp.GetRequiredService<IClaimsTransformation>().TransformAsync(principal);
        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = new DefaultHttpContext { User = principal, RequestServices = sp };
        try { return await action(sp); }
        finally { accessor.HttpContext = previous; }
    }

    public Task RunAsAsync(TestUser user, Func<IServiceProvider, Task> action) =>
        RunAsAsync<object?>(user, async sp => { await action(sp); return null; });

    /// <summary>Direct database access for arranging and asserting (bypasses authorization).</summary>
    public async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && UsingSqlServer)
        {
            try
            {
                using var scope = Services.CreateScope();
                scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeleted();
            }
            catch { /* best effort */ }
        }
        base.Dispose(disposing);
        if (_sqlitePath is not null)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(_sqlitePath); } catch { /* best effort */ }
        }
    }
}
