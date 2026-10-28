using AssetTracker.Core.Security;
using AssetTracker.Infrastructure.Demo;
using AssetTracker.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssetTracker.Web.Demo;

/// <summary>Program.cs hooks for the local demo mode (WP5.1). Nothing here runs when demo mode is off.</summary>
public static class DemoWebSetup
{
    /// <summary>
    /// Reads <c>Demo:Enabled</c>; throws outside Development. When on, adds the in-memory demo configuration.
    /// Call before <c>AddDatabaseSettings()</c>. Returns null when demo mode is off.
    /// </summary>
    public static DemoOptions? AddDemoConfiguration(this WebApplicationBuilder builder)
    {
        var demo = DemoOptions.Resolve(builder.Configuration, builder.Environment);
        if (demo is not null) builder.Configuration.AddDemoDefaults();
        return demo;
    }

    /// <summary>
    /// Demo services: SQLite, demo adapters and jobs (<see cref="DemoServiceCollectionExtensions.AddAssetTrackerDemo"/>),
    /// the demo authentication scheme as the Windows scheme (instead of Negotiate), and the demo <see cref="ICurrentUser"/>.
    /// Call after the production registrations.
    /// </summary>
    public static IServiceCollection AddDemoWeb(this IServiceCollection services, DemoOptions demo, IConfiguration config)
    {
        services.AddAssetTrackerDemo(demo, config);
        services.AddSingleton<DemoIdentityCookie>();
        // The demo scheme takes Negotiate's place behind the app's policy scheme, so the super admin cookie works here too.
        services.Configure<AppAuthenticationOptions>(o => o.WindowsScheme = DemoAuthenticationHandler.SchemeName);
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, DemoAuthenticationHandler>(DemoAuthenticationHandler.SchemeName, _ => { });
        services.Replace(ServiceDescriptor.Scoped<ICurrentUser, DemoCurrentUser>());
        return services;
    }

    /// <summary>The <c>/Demo</c> pages exist only in demo mode: otherwise they get no route (404).</summary>
    public static void ConfigureDemoPages(this PageConventionCollection conventions, bool demoEnabled)
    {
        if (!demoEnabled)
            conventions.AddFolderRouteModelConvention("/Demo", model => model.Selectors.Clear());
    }

    /// <summary>Creates and seeds the demo database (first run) and loads the time-travel offset.</summary>
    public static Task InitializeDemoAsync(this IServiceProvider services) =>
        services.GetRequiredService<DemoDataManager>().InitializeAsync();
}
