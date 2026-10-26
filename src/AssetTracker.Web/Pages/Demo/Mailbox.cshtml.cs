using AssetTracker.Infrastructure.Demo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Demo;

/// <summary>
/// Demo mailbox (demo mode only): the <c>.eml</c> files the pickup relay wrote, newest first, and one selected
/// message's plain-text body (HTML-encoded by Razor; HTML parts are never rendered).
/// </summary>
public class MailboxModel(DemoMailbox mailbox) : PageModel
{
    public IReadOnlyList<DemoMailSummary> Messages { get; private set; } = [];
    public DemoMailMessage? Selected { get; private set; }

    [BindProperty(SupportsGet = true)] public string? Id { get; set; }

    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Messages = mailbox.List();
        if (!string.IsNullOrEmpty(Id))
        {
            Selected = await mailbox.ReadAsync(Id, ct);
            if (Selected is null) return NotFound();
        }
        return Page();
    }

    public IActionResult OnPostClear()
    {
        mailbox.Clear();
        Message = "The demo mailbox was emptied.";
        return RedirectToPage();
    }
}
