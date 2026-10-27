using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Overview;
using AssetTracker.Core.Reporting;
using AssetTracker.Core.Security;
using AssetTracker.Web.Pages.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AssetTracker.Web.Pages;

/// <summary>
/// Overview. For IT and managers it shows figures the reports already compute (scoped to the principal's sites), optionally
/// narrowed to one site, as tiles that admins can switch on or off per role (<see cref="OverviewTileService"/>). Others see
/// a short notice.
/// </summary>
public class IndexModel(
    ICurrentUser user, ReportService reports, OverviewService overview, OverviewTileService tiles, LendingService lending,
    AccessScope access, AppDbContext db) : PageModel
{
    public const int OverdueRows = 8;
    public const int DueSoonRows = 8;
    public const int DueSoonDays = 7;
    public const int StockRows = 5;
    public const int FindRows = 8;

    /// <summary>Limits every tile to one site (query string <c>site</c>).</summary>
    [BindProperty(SupportsGet = true, Name = "site")] public int? SiteFilter { get; set; }

    public string UserName => user.Name;
    public AppRoles Roles => user.Roles;
    public bool CanRead => user.IsIt || user.IsManager;
    public bool CanEdit => user.IsIt;

    public IReadOnlySet<string> Visible { get; private set; } = new HashSet<string>();
    public List<SelectListItem> Sites { get; private set; } = [];

    public InventorySummary? Summary { get; private set; }
    public IReadOnlyList<LoanRow> Overdue { get; private set; } = [];
    public int OverdueTotal { get; private set; }
    public IReadOnlyList<LoanRow> DueSoon { get; private set; } = [];
    public int DueSoonTotal { get; private set; }
    public IReadOnlyList<PoolBalanceRow> LowStock { get; private set; } = [];
    public int PoolCount { get; private set; }
    public int PoolsBelowMinimum { get; private set; }
    public AttentionSummary? Attention { get; private set; }

    /// <summary>Assets per type and state (retired left out), then pools per type.</summary>
    public IReadOnlyList<TypeRow> AssetTypes { get; private set; } = [];
    public IReadOnlyList<PoolTypeRow> PoolTypes { get; private set; } = [];

    public sealed record TypeRow(string Type, int Available, int CheckedOut, int Unavailable)
    {
        public int Total => Available + CheckedOut + Unavailable;
    }

    public sealed record PoolTypeRow(string Type, int Pools, int Available, int OnHand);

    public bool Show(string tile) => Visible.Contains(tile);

    public int AssetsAvailable => AssetCount(AssetState.Available);
    public int AssetsCheckedOut => AssetCount(AssetState.CheckedOut);
    public int AssetsUnavailable => AssetCount(AssetState.Unavailable);
    public int AssetsTotal => AssetsAvailable + AssetsCheckedOut + AssetsUnavailable;
    public int PoolAvailable => Summary?.Pools.Sum(p => p.Available) ?? 0;
    public int PoolOnHand => Summary?.Pools.Sum(p => p.OnHand) ?? 0;

    private int AssetCount(AssetState state) => Summary?.Assets.Where(a => a.State == state).Sum(a => a.Count) ?? 0;


    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!CanRead) return;
        Visible = await tiles.VisibleForCurrentUserAsync(ct);
        var scope = await access.GetAsync(ct);
        Sites = await InventoryUi.SiteOptionsAsync(db, scope, ct);
        // A site the principal cannot read (or that does not exist) is ignored, never an error or a way to probe.
        if (SiteFilter is { } wanted && !Sites.Any(s => s.Value == wanted.ToString())) SiteFilter = null;
        var filter = new ReportFilter(SiteId: SiteFilter);

        Summary = await reports.GetSummaryAsync(SiteFilter, ct);

        if (Show(OverviewTileKeys.Overdue))
        {
            var overdue = await reports.OverdueAsync(filter with { PageSize = OverdueRows }, ct);
            Overdue = overdue.Items;
            OverdueTotal = overdue.TotalCount;
        }
        if (Show(OverviewTileKeys.DueSoon))
        {
            var soon = await reports.DueSoonAsync(filter with { PageSize = DueSoonRows }, DueSoonDays, ct);
            DueSoon = soon.Items;
            DueSoonTotal = soon.TotalCount;
        }
        if (Show(OverviewTileKeys.PoolStock) || Show(OverviewTileKeys.Attention))
        {
            var pools = await reports.PoolBalancesAsync(filter with { PageSize = Core.Common.Paging.MaxPageSize }, ct);
            PoolCount = pools.TotalCount;
            PoolsBelowMinimum = pools.Items.Count(p => p.IsBelowMinimum);
            // Pools under their minimum first, then by how little is left of what was on hand.
            LowStock = pools.Items
                .OrderBy(p => p.IsBelowMinimum ? 0 : 1)
                .ThenBy(p => p.OnHand == 0 ? 0 : (double)p.Available / p.OnHand).ThenBy(p => p.Available).ThenBy(p => p.Sku)
                .Take(StockRows).ToList();
        }
        if (Show(OverviewTileKeys.Attention) && user.IsIt)
            Attention = await overview.GetAttentionAsync(SiteFilter, ct);
        if (Show(OverviewTileKeys.ByType))
        {
            AssetTypes = Summary.Assets.Where(a => a.State != AssetState.Retired).GroupBy(a => a.Type).OrderBy(g => g.Key)
                .Select(g => new TypeRow(g.Key, g.Where(a => a.State == AssetState.Available).Sum(a => a.Count),
                    g.Where(a => a.State == AssetState.CheckedOut).Sum(a => a.Count),
                    g.Where(a => a.State == AssetState.Unavailable).Sum(a => a.Count))).ToList();
            PoolTypes = Summary.Pools.GroupBy(p => p.Type).OrderBy(g => g.Key)
                .Select(g => new PoolTypeRow(g.Key, g.Sum(p => p.Pools), g.Sum(p => p.Available), g.Sum(p => p.OnHand))).ToList();
        }
    }

    /// <summary>
    /// Suggestions for the "Find, check out and return" box (IT only): assets and pools (with the actions that make sense
    /// for their state) and people. At least two characters.
    /// </summary>
    public async Task<IActionResult> OnGetFindAsync(string? q, int? site, CancellationToken ct)
    {
        if (!user.IsIt) return Forbid();
        var term = q?.Trim();
        if (string.IsNullOrEmpty(term) || term.Length < 2) return new JsonResult(Array.Empty<object>());

        var items = await lending.SearchItemsAsync(term, null, site, FindRows, ct);
        var people = await lending.SearchPeopleAsync(term, eligibleOnly: false, take: 4, ct: ct);
        var rows = items.Select(i => (object)new
        {
            kind = i.Kind, id = i.Id, key = i.Key, type = i.Type, title = i.Title, description = i.Description,
            siteKey = i.SiteKey, siteName = i.SiteName, canCheckOut = i.CanCheckOut, status = i.Status, holder = i.Holder,
            loanId = i.LoanId, available = i.Available,
        }).Concat(people.Select(p => (object)new
        {
            kind = "person", id = p.Id, key = p.Username, name = p.DisplayName, email = p.Email, jobTitle = p.JobTitle,
            siteKey = p.SiteKey, siteName = p.SiteName, active = p.IsActive,
        }));
        return new JsonResult(rows);
    }
}
