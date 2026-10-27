using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Security;
using AssetTracker.Core.Time;
using AssetTracker.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AssetTracker.Web.Pages.Loans;

/// <summary>Current (open) loans, scoped by site access.</summary>
[Authorize(Policy = Policies.InventoryRead)]
public class IndexModel(LoanHistoryService loans, AccessScope access, AppDbContext db, ICurrentUser user, SiteCalendar calendar)
    : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int? SiteId { get; set; }
    [BindProperty(SupportsGet = true)] public bool Overdue { get; set; }
    [BindProperty(SupportsGet = true, Name = "inactive")] public bool InactiveHolder { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public PagedResult<Loan> Results { get; private set; } = null!;
    public List<SelectListItem> Sites { get; private set; } = [];
    public bool CanEdit => user.IsIt;
    /// <summary>Each site's local today; loan status uses the loan's site (asset's current site / pool's site).</summary>
    public IReadOnlyDictionary<int, DateOnly> SiteToday { get; private set; } = new Dictionary<int, DateOnly>();

    [TempData] public string? Message { get; set; }

    public PagerModel Pager => PagerModel.From(Results, new Dictionary<string, object?>
    {
        ["q"] = Q, ["siteId"] = SiteId, ["overdue"] = Overdue ? "true" : null, ["inactive"] = InactiveHolder ? "true" : null,
    });

    public bool IsOverdue(Loan l) => l.IsOpen && OverduePolicy.IsOverdue(l.DueDate, TodayFor(l));

    /// <summary>"due today" on the due date itself (D4: already overdue, 0 days late), "overdue" after it.</summary>
    public string? DueStatus(Loan l) =>
        !IsOverdue(l) ? null : OverduePolicy.DaysOverdue(l.DueDate!.Value, TodayFor(l)) == 0 ? "due today" : "overdue";

    private DateOnly TodayFor(Loan l) => SiteToday[l.Asset?.SiteId ?? l.Pool!.SiteId];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Results = await loans.SearchAsync(new LoanSearch(SiteId, Overdue, Q, Page: PageNumber, InactiveHolder: InactiveHolder), ct);
        SiteToday = (await calendar.AllSitesAsync(ct)).ToDictionary(s => s.SiteId, s => s.Today);
        Sites = await InventoryUi.SiteOptionsAsync(db, await access.GetAsync(ct), ct);
    }
}
