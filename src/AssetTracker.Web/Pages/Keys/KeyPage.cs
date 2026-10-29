using AssetTracker.Core.Keys;
using AssetTracker.Core.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Keys;

public abstract class KeyPage(KeyAccessService keys) : PageModel
{
    protected KeyAccessService Keys => keys;
    public KeyAccessBoard Board { get; private set; } = null!;
    [TempData] public string? Message { get; set; }
    public string? Error { get; private set; }
    public virtual async Task OnGetAsync(CancellationToken ct) => Board = await keys.BoardAsync(ct);
    protected virtual IActionResult AfterSave(string? page) => page is null ? RedirectToPage() : RedirectToPage(page);
    protected async Task<IActionResult> Save(Func<Task> change, string? page = null)
    {
        try
        {
            if (!ModelState.IsValid) throw new DomainException("keys.form", "Some fields are missing or invalid. Check dates and selections.");
            await change();
            Message = "Saved.";
            return AfterSave(page);
        }
        catch (DomainException ex)
        {
            Error = ex.Message;
            Response.StatusCode = ex is ConflictException ? 409 : 400;
            await OnGetAsync(HttpContext.RequestAborted);
            return Page();
        }
    }
}
