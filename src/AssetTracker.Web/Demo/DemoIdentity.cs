using System.Security.Claims;
using AssetTracker.Infrastructure.Demo;
using Microsoft.AspNetCore.DataProtection;

namespace AssetTracker.Web.Demo;

/// <summary>
/// Who the demo user is acting as: Windows-style name and SID (matches a seeded person). Their roles are not part of
/// the identity: like every Windows user they come from <c>RoleAssignments</c> (the /Demo switcher changes those).
/// </summary>
public sealed record DemoIdentity(string UserName, string Sid)
{
    /// <summary>
    /// The claims the real Windows (Negotiate) path produces that the app uses: Name and PrimarySid, so the real
    /// <c>AppRoleClaimsTransformation</c>, policies and <c>AccessScope</c> run unchanged.
    /// </summary>
    public ClaimsPrincipal ToPrincipal(string authenticationType)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, UserName), new(ClaimTypes.PrimarySid, Sid) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }
}

/// <summary>
/// Stores the "act as person" choice in a cookie (HttpOnly, SameSite=Strict, Secure only over HTTPS), protected
/// with Data Protection. Without a valid cookie the default demo user applies (<see cref="DemoOptions.UserName"/>,
/// the demo "you" person).
/// </summary>
public sealed class DemoIdentityCookie(IDataProtectionProvider protection, DemoOptions options)
{
    public const string CookieName = "ats_demo_identity";

    private readonly IDataProtector _protector = protection.CreateProtector("AssetTracker.Demo.Identity.v2");

    public DemoIdentity Default => new(options.UserName, DemoDefaults.YouSid);

    public DemoIdentity Read(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value))
            return Default;
        try
        {
            var parts = _protector.Unprotect(value).Split('|', 2);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
                return new DemoIdentity(parts[1], parts[0]);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Key ring changed (e.g. the data folder was deleted): fall back to the default user.
        }
        return Default;
    }

    public void Write(HttpContext context, DemoIdentity identity)
    {
        var payload = $"{identity.Sid}|{identity.UserName}";
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
