using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AssetTracker.Web.Security;

/// <summary>Which scheme signs in Windows users: Negotiate in production, the test scheme in Testing, the demo scheme in demo mode.</summary>
public class AppAuthenticationOptions
{
    public string WindowsScheme { get; set; } = NegotiateDefaults.AuthenticationScheme;
}

/// <summary>
/// Two ways to sign in (§2 of <c>docs/plans/system-roles-and-super-admin.md</c>): Windows, and the built-in super admin
/// on <c>/superadmin</c>. The default scheme is a policy scheme that forwards to the super admin cookie when that
/// cookie is present and otherwise to the Windows scheme (<see cref="AppAuthenticationOptions.WindowsScheme"/>).
/// </summary>
public static class AppAuthentication
{
    public const string PolicyScheme = "App";
    public const string SuperAdminScheme = SuperAdmin.AuthenticationType;

    /// <summary>Production: the <c>__Host-</c> prefix makes the browser require Secure, Path=/ and no Domain.</summary>
    public const string ProductionCookieName = "__Host-ats-superadmin";

    /// <summary>Testing and the local demo run on plain HTTP, where a <c>__Host-</c> cookie would be rejected.</summary>
    public const string PlainHttpCookieName = "ats-superadmin";

    public const string IssuedAtClaim = "ats:issued-at";
    public const string SecurityStampClaim = "ats:security-stamp";

    /// <summary>Rate-limiter policy on the super admin sign-in and password change posts (per IP address).</summary>
    public const string RateLimitPolicy = "superadmin-signin";

    public const string SignInPath = "/superadmin";
    public const string AccountPath = "/superadmin/account";
    public const string SignOutPath = "/superadmin/signout";

    /// <summary>
    /// Registers the policy scheme (default for authenticate, challenge and forbid) and the <c>SuperAdmin</c> cookie
    /// scheme. The Windows scheme itself is added by the caller (Negotiate, test or demo handler).
    /// </summary>
    public static AuthenticationBuilder AddAppAuthentication(this IServiceCollection services, bool plainHttp)
    {
        var cookieName = plainHttp ? PlainHttpCookieName : ProductionCookieName;
        services.AddOptions<AppAuthenticationOptions>();
        services.AddScoped<SuperAdminCookieEvents>();
        services.AddSingleton<IPostConfigureOptions<CookieAuthenticationOptions>, SuperAdminCookieClock>();
        return services.AddAuthentication(PolicyScheme)
            .AddPolicyScheme(PolicyScheme, "Windows or super admin", o => o.ForwardDefaultSelector = context =>
                context.Request.Cookies.ContainsKey(cookieName)
                    ? SuperAdminScheme
                    : context.RequestServices.GetRequiredService<IOptions<AppAuthenticationOptions>>().Value.WindowsScheme)
            .AddCookie(SuperAdminScheme, o =>
            {
                o.Cookie.Name = cookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = plainHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                o.Cookie.Path = "/";
                o.Cookie.IsEssential = true;
                o.LoginPath = SignInPath;
                o.LogoutPath = SignOutPath;
                o.AccessDeniedPath = SignInPath;
                // Idle limit: the ticket expires 30 minutes after the last renewal (sliding).
                o.ExpireTimeSpan = SuperAdmin.IdleTimeout;
                o.SlidingExpiration = true;
                o.EventsType = typeof(SuperAdminCookieEvents);
            });
    }

    /// <summary>At most <see cref="SuperAdmin.RateLimitPermits"/> posts per IP address per 15 minutes; GETs are not limited.</summary>
    public static IServiceCollection AddSuperAdminRateLimiter(this IServiceCollection services) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimitPolicy, context => HttpMethods.IsPost(context.Request.Method)
                ? RateLimitPartition.GetSlidingWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = SuperAdmin.RateLimitPermits,
                        Window = SuperAdmin.RateLimitWindow,
                        SegmentsPerWindow = 15,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    })
                : RateLimitPartition.GetNoLimiter("get"));
            o.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                await context.HttpContext.Response.WriteAsync(
                    "Too many sign-in attempts from this address. Try again in 15 minutes.", ct);
            };
        });

    /// <summary>The identity the sign-in page puts in the cookie: name <c>superadmin (local)</c>, no SID, no roles.</summary>
    public static ClaimsPrincipal CreatePrincipal(string securityStamp, DateTimeOffset issuedAt) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, SuperAdmin.DisplayName),
            new Claim(IssuedAtClaim, issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new Claim(SecurityStampClaim, securityStamp),
        ], SuperAdmin.AuthenticationType, ClaimTypes.Name, ClaimTypes.Role));

    public static DateTimeOffset? IssuedAt(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirst(IssuedAtClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s)
            : null;
}

/// <summary>Whether a principal is the signed-in super admin (an identity from the <c>SuperAdmin</c> cookie scheme).</summary>
public static class SuperAdminPrincipal
{
    public static bool Is(ClaimsPrincipal? principal) =>
        principal?.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == SuperAdmin.AuthenticationType) == true;
}

/// <summary>
/// Cookie events of the super admin: the per-request session check, and 403 instead of a redirect for forbidden pages.
/// </summary>
public sealed class SuperAdminCookieEvents(SuperAdminService service, IClock clock) : CookieAuthenticationEvents
{
    /// <summary>
    /// Ends the session when the password was changed or reset or the account was switched off (security stamp), or
    /// 8 hours after sign-in. The 30-minute idle limit is the cookie's sliding expiry.
    /// </summary>
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var issuedAt = principal is null ? null : AppAuthentication.IssuedAt(principal);
        var valid = principal is not null && issuedAt is { } at && clock.UtcNow - at < SuperAdmin.AbsoluteTimeout &&
                    await service.IsSessionValidAsync(principal.FindFirst(AppAuthentication.SecurityStampClaim)?.Value,
                        context.HttpContext.RequestAborted);
        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AppAuthentication.SuperAdminScheme);
        }
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

/// <summary>The super admin cookie's expiry follows <see cref="IClock"/> (so tests and the demo clock drive it).</summary>
public sealed class SuperAdminCookieClock(IClock clock) : IPostConfigureOptions<CookieAuthenticationOptions>
{
    public void PostConfigure(string? name, CookieAuthenticationOptions options)
    {
        if (name == AppAuthentication.SuperAdminScheme) options.TimeProvider = new ClockTimeProvider(clock);
    }

    private sealed class ClockTimeProvider(IClock clock) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock.UtcNow;
    }
}
