using System.ComponentModel.DataAnnotations;
using AssetTracker.Core.Admin;
using AssetTracker.Core.Common;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

[Authorize(Policy = Policies.AdminOnly)]
public class SettingsModel(AdminService admin) : PageModel
{
    [BindProperty, Required, StringLength(320), Display(Name = "IT mailbox / distribution list")]
    public string? ItMailbox { get; set; }

    [TempData]
    public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => ItMailbox = await admin.GetItMailboxAsync(ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }
        try
        {
            await admin.SetItMailboxAsync(ItMailbox, ct);
            Message = "Settings saved.";
            return RedirectToPage();
        }
        catch (DomainException ex)
        {
            this.Show(ex);
            return Page();
        }
    }
}
