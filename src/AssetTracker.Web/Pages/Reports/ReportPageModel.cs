using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Reporting;
using AssetTracker.Core.Security;
using AssetTracker.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AssetTracker.Web.Pages.Reports;

/// <summary>
/// Shared filters, site dropdown, pager route values and CSV result for report pages. Authorization is enforced
/// twice: the InventoryRead policy here, and <see cref="ReportService"/> scoping every query and export through
/// <see cref="AccessScope"/> (a manipulated <c>siteId</c> can only narrow what the principal may already read).
/// </summary>
[Authorize(Policy = Policies.InventoryRead)]
public abstract class ReportPageModel(AccessScope access, AppDbContext db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int? SiteId { get; set; }
    [BindProperty(SupportsGet = true)] public AssetType? Type { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public List<SelectListItem> Sites { get; private set; } = [];

    /// <summary>Label of the date range filter, or null when the report has none.</summary>
    public virtual string? DateRangeLabel => null;

    public virtual string SearchPlaceholder => "Holder, username, email, tag or SKU";

    protected virtual ReportFilter Filter => new(Q, SiteId, Type, null, From, To, PageNumber);

    /// <summary>Current filters as query-string values (paging excluded).</summary>
    public virtual Dictionary<string, object?> FilterValues => new()
    {
        ["q"] = Q, ["siteId"] = SiteId, ["type"] = Type,
        ["from"] = From?.ToString("yyyy-MM-dd"), ["to"] = To?.ToString("yyyy-MM-dd"),
    };

    /// <summary>Route values for an export link that preserves the current filters.</summary>
    public Dictionary<string, string> ExportRoute(string handler = "Export")
    {
        var route = FilterValues.Where(kv => kv.Value is not null && kv.Value.ToString() != "")
            .ToDictionary(kv => kv.Key, kv => kv.Value!.ToString()!);
        route["handler"] = handler;
        return route;
    }

    protected PagerModel PagerFor<T>(PagedResult<T> r) => PagerModel.From(r, FilterValues);

    protected async Task LoadSitesAsync(CancellationToken ct) =>
        Sites = await InventoryUi.SiteOptionsAsync(db, await access.GetAsync(ct), ct);

    protected FileContentResult Csv(CsvExport export) => File(export.Content, CsvExport.ContentType, export.FileName);
}
