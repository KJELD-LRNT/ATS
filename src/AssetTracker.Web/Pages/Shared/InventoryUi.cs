using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Web.Pages.Shared;

/// <summary>Model for the shared <c>_Pager</c> partial: the current page plus the query string to keep.</summary>
public sealed record PagerModel(int Page, int PageCount, int TotalCount, IReadOnlyDictionary<string, string> RouteValues)
{
    public static PagerModel From<T>(PagedResult<T> r, IEnumerable<KeyValuePair<string, object?>> filters) =>
        new(r.Page, r.PageCount, r.TotalCount, filters
            .Where(kv => kv.Value is not null && kv.Value.ToString() != "")
            .ToDictionary(kv => kv.Key, kv => kv.Value!.ToString()!));

    public Dictionary<string, string> For(int page) => new(RouteValues) { ["p"] = page.ToString() };
}

/// <summary>Dropdown helpers for inventory pages.</summary>
public static class InventoryUi
{
    /// <summary>Active sites, optionally limited to what the principal may read (for filters).</summary>
    public static async Task<List<SelectListItem>> SiteOptionsAsync(AppDbContext db, SiteAccess? limitTo = null,
        CancellationToken ct = default)
    {
        var sites = await db.Sites.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Key).ToListAsync(ct);
        return sites.Where(s => limitTo is null || limitTo.CanRead(s.Id))
            .Select(s => new SelectListItem($"{s.Key} – {s.Name}", s.Id.ToString())).ToList();
    }

    public static List<SelectListItem> EnumOptions<T>(IEnumerable<T>? only = null) where T : struct, Enum =>
        (only ?? Enum.GetValues<T>()).Select(v => new SelectListItem(v.ToString(), v.ToString())).ToList();

    public static string Describe(StockEventType t) => t switch
    {
        StockEventType.CountAdjustment => "Count adjustment",
        StockEventType.TransferOut => "Transfer out",
        StockEventType.TransferIn => "Transfer in",
        _ => t.ToString(),
    };
}
