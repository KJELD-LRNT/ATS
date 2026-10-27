using AssetTracker.Core.Security;

namespace AssetTracker.Core.Overview;

/// <summary>Keys of the overview's tiles (the cards and figures on the home page).</summary>
public static class OverviewTileKeys
{
    public const string Find = "find";
    public const string KpiLoans = "kpi-loans";
    public const string KpiOverdue = "kpi-overdue";
    public const string KpiAssets = "kpi-assets";
    public const string KpiPools = "kpi-pools";
    public const string Attention = "attention";
    public const string Overdue = "overdue";
    public const string DueSoon = "due-soon";
    public const string ByType = "by-type";
    public const string PoolStock = "pool-stock";
    public const string Links = "links";
}

/// <summary>
/// One tile. <see cref="AllowedRoles"/> is what the tile may ever be shown to (a tile with mail or sync status is for IT
/// only, whatever the admin ticks); <see cref="DefaultRoles"/> is used until an admin changes it.
/// </summary>
public sealed record TileDefinition(string Key, string Title, string Description, AppRoles AllowedRoles, AppRoles DefaultRoles);

public static class OverviewTiles
{
    private const AppRoles Both = AppRoles.It | AppRoles.Manager;

    /// <summary>All tiles in page order. Admin → Overview tiles switches them on or off per role (IT, manager).</summary>
    public static readonly IReadOnlyList<TileDefinition> All =
    [
        new(OverviewTileKeys.Find, "Find, check out and return",
            "Search box for assets, pools and people with check-out and return shortcuts (IT); asset search for managers.", Both, Both),
        new(OverviewTileKeys.KpiLoans, "Open loans", "Number of open loans and pool units out.", Both, Both),
        new(OverviewTileKeys.KpiOverdue, "Overdue or due today", "Number of loans that are overdue or due today.", Both, Both),
        new(OverviewTileKeys.KpiAssets, "Tagged assets available",
            "Available assets with the split into checked out and unavailable.", Both, Both),
        new(OverviewTileKeys.KpiPools, "Pool units available", "Units on the shelf across the quantity pools.", Both, Both),
        new(OverviewTileKeys.Attention, "Needs attention",
            "Things to act on: loans held by people who left, unavailable assets, pools below minimum, failed mails.",
            AppRoles.It, AppRoles.It),
        new(OverviewTileKeys.Overdue, "Overdue and due today", "The loans that are overdue, most overdue first.", Both, Both),
        new(OverviewTileKeys.DueSoon, "Due soon", "Loans that fall due in the next 7 days.", Both, Both),
        new(OverviewTileKeys.ByType, "Inventory by type", "Available, out and unavailable per asset type, and pools per type.", Both, Both),
        new(OverviewTileKeys.PoolStock, "Pool stock", "The pools lowest on stock, flagged when below their minimum.", Both, Both),
        new(OverviewTileKeys.Links, "Go to", "Shortcuts to the main reports.", Both, AppRoles.Manager),
    ];

    public static TileDefinition? Find(string key) => All.FirstOrDefault(t => t.Key == key);
}
