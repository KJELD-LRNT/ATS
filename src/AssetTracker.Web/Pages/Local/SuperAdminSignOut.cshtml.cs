using AssetTracker.Core.Security;
using AssetTracker.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Local;

/// <summary>
/// <c>/superadmin/signout</c>: POST only (with the antiforgery token every Razor Pages post needs). Ends the super
/// admin session, audited with the IP address. Anonymous, so an expired cookie can still be cleared.
/// </summary>
[AllowAnonymous]
public class SuperAdminSignOutModel(SuperAdminService service) : PageModel
{
    public IActionResult OnGet() => Redirect(AppAuthentication.SignInPath);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (SuperAdminPrincipal.Is(User))
            await service.RecordSignOutAsync(HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        await HttpContext.SignOutAsync(AppAuthentication.SuperAdminScheme);
        return Redirect(AppAuthentication.SignInPath + "?signedOut=true");
    }
}
