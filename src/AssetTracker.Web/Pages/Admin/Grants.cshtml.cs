using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

/// <summary>Manager site grants moved to System roles (on the same row as the person's roles); old links land there.</summary>
[Authorize(Policy = Policies.AdminOnly)]
public class GrantsModel : PageModel
{
    public IActionResult OnGet(string? q) => RedirectToPage("/Admin/SystemRoles", new { q });
}
