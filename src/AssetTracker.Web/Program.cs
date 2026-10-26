using AssetTracker.Core;
using AssetTracker.Core.Security;
using AssetTracker.Infrastructure;
using AssetTracker.Web.Health;
using AssetTracker.Web.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAssetTrackerCore();
builder.Services.AddAssetTrackerInfrastructure(builder.Configuration);

// Integrated Windows Authentication (IIS in-process passes the Windows identity through Negotiate).
// The Testing environment substitutes a simulated Windows principal scheme (see tests).
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
}
builder.Services.Configure<RoleGroupOptions>(builder.Configuration.GetSection("Roles"));
builder.Services.AddTransient<IClaimsTransformation, AppRoleClaimsTransformation>();
builder.Services.AddAuthorization(o =>
{
    // Every page requires an authenticated Windows user unless explicitly opted out.
    o.FallbackPolicy = o.DefaultPolicy;
    o.AddPolicy(Policies.ItOnly, p => p.RequireClaim(AppClaimTypes.Role, nameof(AppRoles.It)));
    o.AddPolicy(Policies.InventoryRead, p => p.RequireClaim(AppClaimTypes.Role, nameof(AppRoles.It), nameof(AppRoles.Manager)));
    o.AddPolicy(Policies.AdminOnly, p => p.RequireClaim(AppClaimTypes.Role, nameof(AppRoles.Admin)));
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddRazorPages(o => o.Conventions.ConfigureFilter(new DomainExceptionFilter()));
builder.Services.AddHealthChecks().AddAssetTrackerReadiness();

// Antiforgery and TempData cookies: HttpOnly, SameSite=Strict, Secure (HTTPS only outside the plain-HTTP test host).
var cookieSecure = builder.Environment.IsEnvironment("Testing") ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
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

var app = builder.Build();

if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseSecurityHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();
// Everything below static files is dynamic (pages, CSV exports with personal data, health): never cache it.
app.UseNoStoreForDynamicResponses();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

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
