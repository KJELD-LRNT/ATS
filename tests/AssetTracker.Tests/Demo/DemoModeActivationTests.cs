using System.Net;
using AssetTracker.Core.Data;
using AssetTracker.Core.Security;
using AssetTracker.Tests.Infrastructure;
using AssetTracker.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssetTracker.Tests.Demo;

/// <summary>Demo mode is opt-in, Development-only, and leaves the production wiring alone when off.</summary>
public class DemoModeActivationTests
{
    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Demo_enabled_outside_development_fails_at_startup(string environment)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.UseSetting("Demo:Enabled", "true");
            b.UseSetting("Demo:DataDirectory", Path.Combine(Path.GetTempPath(), "ats_demo_never_" + Guid.NewGuid().ToString("N")));
        });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var root = ex;
        while (root is not InvalidOperationException && root.InnerException is not null) root = root.InnerException;
        Assert.IsType<InvalidOperationException>(root);
        Assert.Contains("only allowed in the Development environment", root.Message);
    }

    [Fact]
    public async Task Demo_disabled_demo_pages_do_not_exist()
    {
        using var f = new AppFactory();
        var it = f.CreateClientAs(TestUser.It);
        Assert.Equal(HttpStatusCode.NotFound, (await it.GetAsync("/Demo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await it.GetAsync("/Demo/Mailbox")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await it.GetAsync("/Demo/Mailbox?id=x.eml")).StatusCode);

        var home = await (await it.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("DEMO MODE", home);
        using var scope = f.Services.CreateScope();
        Assert.IsType<HttpCurrentUser>(scope.ServiceProvider.GetRequiredService<ICurrentUser>());
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync("Demo"));
    }

    [Fact]
    public async Task Demo_disabled_in_development_keeps_windows_authentication()
    {
        var db = Path.Combine(Path.GetTempPath(), "ats_dev_" + Guid.NewGuid().ToString("N") + ".db");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureTestServices(s =>
            {
                // Only the database is swapped (no SQL Server here); authentication stays as Program.cs set it.
                s.RemoveAll<DbContextOptions<AppDbContext>>();
                s.RemoveAll<AppDbContext>();
                s.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
                s.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={db}"));
            });
        });
        try
        {
            var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
            Assert.Equal(NegotiateDefaults.AuthenticationScheme, (await schemes.GetDefaultAuthenticateSchemeAsync())?.Name);
            Assert.Null(await schemes.GetSchemeAsync("Demo"));

            Assert.Equal(NegotiateDefaults.AuthenticationScheme, (await schemes.GetDefaultChallengeSchemeAsync())?.Name);
            using var scope = factory.Services.CreateScope();
            Assert.IsType<HttpCurrentUser>(scope.ServiceProvider.GetRequiredService<ICurrentUser>());
            Assert.Null(scope.ServiceProvider.GetService<AssetTracker.Infrastructure.Demo.DemoOptions>());
            // No HTTP round trip: the Negotiate handler needs Kestrel connection features that TestServer lacks.
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch { /* best effort */ }
        }
    }
}
