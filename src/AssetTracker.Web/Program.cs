using AssetTracker.Core;
using AssetTracker.Core.Security;
using AssetTracker.Infrastructure;
using AssetTracker.Web.Security;
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
builder.Services.AddHealthChecks();

var app = builder.Build();

if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapRazorPages();

app.Run();

public partial class Program;
