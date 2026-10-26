using AssetTracker.Core.Common;
using AssetTracker.Core.Privacy;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

/// <summary>
/// Client-directed anonymization of inactive people (docs/privacy-and-retention.md). Requires a reason and an
/// explicit confirmation; the service enforces the admin role, inactive state and no open loans, and audits.
/// </summary>
[Authorize(Policy = Policies.AdminOnly)]
public class PrivacyModel(PrivacyService privacy) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public IReadOnlyList<AnonymizationCandidate> Candidates { get; private set; } = [];

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => Candidates = await privacy.SearchInactiveAsync(Q, ct);

    public async Task<IActionResult> OnPostAnonymizeAsync(int personId, string? reason, bool confirm, CancellationToken ct)
    {
        if (!confirm)
        {
            ModelState.AddModelError(string.Empty, "Tick the confirmation box: anonymization cannot be undone.");
            Response.StatusCode = StatusCodes.Status400BadRequest;
        }
        else
        {
            try
            {
                var r = await privacy.AnonymizePersonAsync(personId, reason, ct);
                Message = $"Person {r.PersonId} anonymized as {r.Pseudonym}: {r.LoansScrubbed} loan(s), " +
                          $"{r.OutboxRowsScrubbed} notification(s) scrubbed, {r.GrantsRemoved} grant(s) removed.";
                return RedirectToPage(new { q = Q });
            }
            catch (DomainException ex) { this.Show(ex); }
        }
        Candidates = await privacy.SearchInactiveAsync(Q, ct);
        return Page();
    }
}
