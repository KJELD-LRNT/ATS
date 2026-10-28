using AssetTracker.Core.Common;
using AssetTracker.Core.Security;
using AssetTracker.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace AssetTracker.Web.Pages.Local;

/// <summary>
/// <c>/superadmin/account</c>: the signed-in super admin's own page: account state and the password change (current
/// password required; at least 16 characters, not the demo password). Only the super admin can open it.
/// </summary>
[Authorize(Policy = Policies.SuperAdminOnly)]
[EnableRateLimiting(AppAuthentication.RateLimitPolicy)]
public class SuperAdminAccountModel(SuperAdminService service) : PageModel
{
    [BindProperty] public string? CurrentPassword { get; set; }
    [BindProperty] public string? NewPassword { get; set; }
    [BindProperty] public string? ConfirmPassword { get; set; }

    [TempData] public string? Message { get; set; }

    public SuperAdminStatus Status { get; private set; } = SuperAdminStatus.Missing;
    public DateTimeOffset? SignedInAt { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            var stamp = await service.ChangePasswordAsync(CurrentPassword, NewPassword, ConfirmPassword, ct);
            // The stamp changed, which ends every other session; this browser gets a fresh cookie with the new stamp.
            // The sign-in time stays, so the 8-hour limit still counts from the real sign-in.
            var issuedAt = AppAuthentication.IssuedAt(User) ?? DateTimeOffset.UnixEpoch;
            await HttpContext.SignInAsync(AppAuthentication.SuperAdminScheme,
                AppAuthentication.CreatePrincipal(stamp, issuedAt), new AuthenticationProperties { IsPersistent = false });
            Message = "Your password is changed. Other super admin sessions have ended.";
            return RedirectToPage();
        }
        catch (DomainException ex)
        {
            Error = ex.Message;
        }
        CurrentPassword = NewPassword = ConfirmPassword = null;
        await LoadAsync(ct);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Status = await service.GetStatusAsync(ct);
        SignedInAt = AppAuthentication.IssuedAt(User);
    }
}
