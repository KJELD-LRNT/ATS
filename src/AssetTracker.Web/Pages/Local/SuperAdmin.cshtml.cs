using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Security;
using AssetTracker.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace AssetTracker.Web.Pages.Local;

/// <summary>
/// <c>/superadmin</c>: username and password sign-in for the built-in super admin (not an AD account). Open to
/// everyone; the posts are rate-limited per IP address and the account locks after 5 failures.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting(AppAuthentication.RateLimitPolicy)]
public class SuperAdminModel(SuperAdminService service, IClock clock) : PageModel
{
    /// <summary>Shown for every refused sign-in: never says whether the username or the password was wrong.</summary>
    public const string FailedMessage =
        "Sign-in failed. Check the username and password. After 5 failed attempts the account is locked for 15 minutes.";

    [BindProperty] public string? UserName { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true)] public bool SignedOut { get; set; }

    public SuperAdminStatus Status { get; private set; } = SuperAdminStatus.Missing;
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (SuperAdminPrincipal.Is(User)) return Redirect(AppAuthentication.AccountPath);
        // A super admin cookie that no longer works (expired, password changed): remove it, so this browser can use
        // Windows sign-in again.
        if (Request.Cookies.Keys.Any(k => k is AppAuthentication.ProductionCookieName or AppAuthentication.PlainHttpCookieName))
            await HttpContext.SignOutAsync(AppAuthentication.SuperAdminScheme);
        Status = await service.GetStatusAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await service.SignInAsync(UserName, Password, ip, ct);
        if (result.Result == SuperAdminSignInResult.Success)
        {
            var principal = AppAuthentication.CreatePrincipal(result.SecurityStamp!, clock.UtcNow);
            await HttpContext.SignInAsync(AppAuthentication.SuperAdminScheme, principal,
                new AuthenticationProperties { IsPersistent = false });
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/Admin/Settings");
        }
        Password = null;
        Error = FailedMessage;
        Status = await service.GetStatusAsync(ct);
        return Page();
    }
}
