using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Security;
using AssetTracker.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AssetTracker.Web.Pages.Loans;

/// <summary>Current (open) loans, scoped by site access.</summary>
[Authorize(Policy = Policies.InventoryRead)]
public class IndexModel(LoanHistoryService loans, AccessScope access, AppDbContext db, ICurrentUser user, IClock clock)
    : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int? SiteId { get; set; }
    [BindProperty(SupportsGet = true)] public bool Overdue { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public PagedResult<Loan> Results { get; private set; } = null!;
    public List<SelectListItem> Sites { get; private set; } = [];
    public bool CanEdit => user.IsIt;
    public DateOnly Today => clock.LocalToday;

    [TempData] public string? Message { get; set; }

    public PagerModel Pager => PagerModel.From(Results, new Dictionary<string, object?>
    {
        ["q"] = Q, ["siteId"] = SiteId, ["overdue"] = Overdue ? "true" : null,
    });

    public bool IsOverdue(Loan l) => l.IsOpen && OverduePolicy.IsOverdue(l.DueDate, Today);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Results = await loans.SearchAsync(new LoanSearch(SiteId, Overdue, Q, Page: PageNumber), ct);
        Sites = await InventoryUi.SiteOptionsAsync(db, await access.GetAsync(ct), ct);
    }
}
