using AssetTracker.Core.Common;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Loans;

[Authorize(Policy = Policies.ItOnly)]
public class ReturnModel(LendingService lending, LoanHistoryService history) : PageModel
{
    public class InputModel
    {
        public int? Quantity { get; set; }

        /// <summary>UTC; blank = now.</summary>
        public DateTime? ReturnedAt { get; set; }
        public string? Condition { get; set; }
        public string? Note { get; set; }
        public bool MarkUnavailable { get; set; }
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public LoanHistory History { get; private set; } = null!;

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(int id, CancellationToken ct)
    {
        History = await history.GetAsync(id, ct);
        Input.Quantity = History.Loan.OutstandingQuantity;
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken ct)
    {
        try
        {
            DateTimeOffset? at = Input.ReturnedAt is { } t
                ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), TimeSpan.Zero)
                : null;
            var r = await lending.ReturnAsync(id, Input.Quantity, at, Input.Condition, Input.Note, Input.MarkUnavailable, ct);
            Message = r.FullyReturned
                ? $"Returned {r.Event.Quantity}; the loan is closed."
                : $"Returned {r.Event.Quantity}; {r.Loan.OutstandingQuantity} still outstanding.";
            return RedirectToPage("Details", new { id });
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError("", ex.Message);
        }
        History = await history.GetAsync(id, ct);
        return Page();
    }
}
