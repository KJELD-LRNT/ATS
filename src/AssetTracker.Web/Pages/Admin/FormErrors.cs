using AssetTracker.Core.Common;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

internal static class FormErrors
{
    /// <summary>Shows a business-rule error on the re-rendered form, with a 400/409 status.</summary>
    public static void Show(this PageModel page, DomainException ex)
    {
        page.ModelState.AddModelError(string.Empty, ex.Message);
        page.Response.StatusCode = ex is ConflictException ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
    }
}
