using AssetTracker.Core;
using AssetTracker.Core.Security;
using AssetTracker.Infrastructure;
using AssetTracker.Infrastructure.Configuration;
using AssetTracker.Infrastructure.Security;
using AssetTracker.Web.Demo;
using AssetTracker.Web.Health;
using AssetTracker.Web.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);
// Local demo mode (WP5.1, docs/testing-guide.md): only in Development; throws if enabled anywhere else. Null when off.
var demo = builder.AddDemoConfiguration();
// Settings saved on Admin → Settings (AppSettings table) override the appsettings files. Keep this the last source.
builder.Configuration.AddDatabaseSettings();

builder.Services.AddAssetTrackerCore();
builder.Services.AddAssetTrackerInfrastructure(builder.Configuration);
// Shared key ring with the worker (encrypted SMTP password); also protects antiforgery/TempData cookies.
builder.Services.AddAssetTrackerDataProtection(builder.Configuration, builder.Environment);

// Testing and the local demo run on plain HTTP (cookies without Secure / the __Host- prefix there).
var plainHttp = builder.Environment.IsEnvironment("Testing") || demo is not null;

// Two ways to sign in: Windows, and the built-in super admin on /superadmin (cookie). The default scheme forwards to
// the super admin cookie when it is present, otherwise to the Windows scheme (AppAuthenticationOptions.WindowsScheme).
builder.Services.AddAppAuthentication(plainHttp);
// Integrated Windows Authentication (IIS in-process passes the Windows identity through Negotiate).
// The Testing environment substitutes a simulated Windows principal scheme (see tests); demo mode its own scheme.
if (!plainHttp)
{
    builder.Services.AddAuthentication().AddNegotiate();
}
// At most 10 super admin sign-in posts per IP address per 15 minutes (on top of the account lockout).
builder.Services.AddSuperAdminRateLimiter();
builder.Services.Configure<RoleGroupOptions>(builder.Configuration.GetSection("Roles"));
builder.Services.AddTransient<IClaimsTransformation, AppRoleClaimsTransformation>();
builder.Services.AddAuthorization(o =>
{
    // Every page requires an authenticated Windows user unless explicitly opted out.
    o.FallbackPolicy = o.DefaultPolicy;
    o.AddPolicy(Policies.ItOnly, p => p.RequireClaim(AppClaimTypes.Role, nameof(AppRoles.It)));
    o.AddPolicy(Policies.InventoryRead, p => p.RequireClaim(AppClaimTypes.Role, nameof(AppRoles.It), nameof(AppRoles.Manager)));
    o.AddPolicy(Policies.AdminOnly, p => p.RequireClaim(AppClaimTypes.Role, nameof(AppRoles.Admin)));
    o.AddPolicy(Policies.ItOrAdmin, p => p.RequireClaim(AppClaimTypes.Role, nameof(AppRoles.It), nameof(AppRoles.Admin)));
    o.AddPolicy(Policies.WorkplaceRead, p => p.RequireClaim(AppClaimTypes.Role,
        nameof(AppRoles.It), nameof(AppRoles.Facility), nameof(AppRoles.Manager)));
    o.AddPolicy(Policies.WorkplaceEdit, p => p.RequireClaim(AppClaimTypes.Role, nameof(AppRoles.It), nameof(AppRoles.Facility)));
    // Only the signed-in super admin (its own account page: password change).
    o.AddPolicy(Policies.SuperAdminOnly, p => p.RequireAuthenticatedUser().RequireAssertion(c => SuperAdminPrincipal.Is(c.User)));
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddRazorPages(o =>
{
    o.Conventions.ConfigureFilter(new DomainExceptionFilter());
    o.Conventions.ConfigureDemoPages(demo is not null);
})
// No client-side validation scripts are used (CSP). With it off, an empty validation summary renders nothing instead
// of a placeholder with an inline style attribute, which the CSP blocks (a console violation on every form page).
.AddViewOptions(o => o.HtmlHelperOptions.ClientValidationEnabled = false);
builder.Services.AddHealthChecks().AddAssetTrackerReadiness();

// Antiforgery and TempData cookies: HttpOnly, SameSite=Strict, Secure (HTTPS only outside the plain-HTTP test host
// and the plain-HTTP local demo).
var cookieSecure = plainHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = cookieSecure;
});
builder.Services.Configure<CookieTempDataProviderOptions>(o =>
{
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = cookieSecure;
});

// Demo mode replaces the database, directory, relay, clock, key ring, authentication and current user.
if (demo is not null) builder.Services.AddDemoWeb(demo, builder.Configuration);

var app = builder.Build();
// Demo mode: create and seed the SQLite demo database on first run.
if (demo is not null) await app.Services.InitializeDemoAsync();
// First load of database settings before anything reads options (tolerates a database that is not migrated yet).
await app.Services.LoadDatabaseSettingsAsync();

if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseSecurityHeaders();
if (demo is null) app.UseHttpsRedirection();
app.UseStaticFiles();
// Everything below static files is dynamic (pages, CSV exports with personal data, health): never cache it.
app.UseNoStoreForDynamicResponses();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Liveness: the process answers; no dependencies are checked. Anonymous for the client's monitoring.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
// Readiness: database, AD sync freshness, mail outbox and worker heartbeat. Minimal JSON, no internals.
// Healthy/Degraded → 200, Unhealthy → 503.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains(ReadinessChecks.Tag),
    ResponseWriter = ReadinessChecks.WriteResponse,
}).AllowAnonymous();
app.MapRazorPages();

app.Run();

public partial class Program;
