using System.Security.Claims;
using AssetTracker.Core.Security;
using AssetTracker.Infrastructure.Demo;
using Microsoft.AspNetCore.DataProtection;

namespace AssetTracker.Web.Demo;

/// <summary>Who the demo user is acting as: Windows-style name, SID (matches a seeded person) and chosen roles.</summary>
public sealed record DemoIdentity(string UserName, string Sid, AppRoles Roles)
{
    /// <summary>
    /// The claims the real Windows (Negotiate) path produces: Name, PrimarySid and one GroupSid per role group, so
    /// the real <c>AppRoleClaimsTransformation</c>, policies and <c>AccessScope</c> run unchanged.
    /// </summary>
    public ClaimsPrincipal ToPrincipal(string authenticationType)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, UserName), new(ClaimTypes.PrimarySid, Sid) };
        if (Roles.HasFlag(AppRoles.It)) claims.Add(new Claim(ClaimTypes.GroupSid, DemoDefaults.ItGroupSid));
        if (Roles.HasFlag(AppRoles.Manager)) claims.Add(new Claim(ClaimTypes.GroupSid, DemoDefaults.ManagerGroupSid));
        if (Roles.HasFlag(AppRoles.Admin)) claims.Add(new Claim(ClaimTypes.GroupSid, DemoDefaults.AdminGroupSid));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }
}

/// <summary>
/// Stores the role switcher's choice in a cookie (HttpOnly, SameSite=Strict, Secure only over HTTPS), protected
/// with Data Protection. Without a valid cookie the default demo user applies (<see cref="DemoOptions.UserName"/>,
/// the demo "you" person, roles IT + admin).
/// </summary>
public sealed class DemoIdentityCookie(IDataProtectionProvider protection, DemoOptions options)
{
    public const string CookieName = "ats_demo_identity";

    private readonly IDataProtector _protector = protection.CreateProtector("AssetTracker.Demo.Identity.v1");

    public DemoIdentity Default => new(options.UserName, DemoDefaults.YouSid, DemoDefaults.DefaultRoles);

    public DemoIdentity Read(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value))
            return Default;
        try
        {
            var parts = _protector.Unprotect(value).Split('|');
            if (parts.Length == 3 && Enum.TryParse<AppRoles>(parts[0], out var roles) &&
                parts[1].Length > 0 && parts[2].Length > 0)
                return new DemoIdentity(parts[2], parts[1], roles);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Key ring changed (e.g. the data folder was deleted): fall back to the default user.
        }
        return Default;
    }

    public void Write(HttpContext context, DemoIdentity identity)
    {
        var payload = $"{(int)identity.Roles}|{identity.Sid}|{identity.UserName}";
        context.Response.Cookies.Append(CookieName, _protector.Protect(payload), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            IsEssential = true,
            Path = "/",
        });
    }
}
