using AssetTracker.Core.Admin;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

/// <summary>
/// The single place for roles: one row per person with any role or manager site grant, with a tick box for each role
/// (IT, Manager, Admin, Facility) and the sites a manager may see. <i>Add a person</i> searches the directory snapshot.
/// Admins and the super admin. Every change is audited by <see cref="RoleAssignmentService"/>.
/// </summary>
[Authorize(Policy = Policies.AdminOnly)]
public class SystemRolesModel(RoleAssignmentService service, AdminService admin) : PageModel
{
    public IReadOnlyList<PersonRolesRow> Rows { get; private set; } = [];
    public IReadOnlyList<PersonRolesRow> Found { get; private set; } = [];

    /// <summary>Sites offered as tick boxes: the active ones (a row also shows inactive sites it already has).</summary>
    public IReadOnlyList<Site> Sites { get; private set; } = [];
    public IReadOnlyList<Site> AllSites { get; private set; } = [];
    public bool AnyAdmin { get; private set; }

    /// <summary>Filter: only people with this role (empty = everyone).</summary>
    [BindProperty(SupportsGet = true)] public string? Role { get; set; }

    /// <summary>Filter: people with a grant for this site or based at it.</summary>
    [BindProperty(SupportsGet = true)] public int? Site { get; set; }

    /// <summary>Filter: also show people who are inactive in the directory (their roles have no effect).</summary>
    [BindProperty(SupportsGet = true)] public bool Inactive { get; set; }

    /// <summary>Add a person: search text.</summary>
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? Warning { get; set; }

    public static IReadOnlyList<(AppRoles Role, string Label, string Help)> RoleColumns =>
    [
        (AppRoles.It, "IT", "Works with inventory, loans, reports, floor plans and the audit log."),
        (AppRoles.Manager, "Manager", "Reads the inventory of the sites ticked on the row."),
        (AppRoles.Admin, "Admin", "Settings, roles, sites, directory sync, asset types and privacy. No inventory by itself."),
        (AppRoles.Facility, "Facility", "Works with the floor plans, physical keys and digital access."),
    ];

    public AppRoles RoleFilter => Enum.TryParse<AppRoles>(Role, out var r) && RoleAssignmentService.Assignable.Contains(r) ? r : AppRoles.None;

    public bool Filtered => RoleFilter != AppRoles.None || Site is not null || Inactive;

    public Task OnGetAsync(CancellationToken ct) => LoadAsync(ct);

    public async Task<IActionResult> OnPostSaveAsync(string directoryId, string[]? roles, int[]? siteIds, CancellationToken ct)
    {
        var wanted = AppRoles.None;
        foreach (var r in roles ?? [])
            if (Enum.TryParse<AppRoles>(r, out var parsed) && RoleAssignmentService.Assignable.Contains(parsed)) wanted |= parsed;
        try
        {
            var change = await service.SaveAsync(directoryId, wanted, siteIds ?? [], ct);
            Message = Describe(change);
            if (change.NoAdminLeft && change.Removed.HasFlag(AppRoles.Admin))
                Warning = "Nobody has the Admin role any more. From now on only the super admin can give roles.";
            return RedirectToPage(new { role = Role, site = Site, inactive = Inactive ? "true" : null, q = Q });
        }
        catch (DomainException ex) { this.Show(ex); }
        await LoadAsync(ct);
        return Page();
    }

    private static string Describe(RoleChange c)
    {
        if (!c.Changed) return $"Nothing changed for {c.PersonName}.";
        var parts = new List<string>();
        if (c.Added != AppRoles.None) parts.Add($"gave {Names(c.Added)}");
        if (c.Removed != AppRoles.None) parts.Add($"removed {Names(c.Removed)}");
        if (c.SitesAdded.Count > 0) parts.Add($"added site {string.Join(", ", c.SitesAdded)}");
        if (c.SitesRemoved.Count > 0) parts.Add($"removed site {string.Join(", ", c.SitesRemoved)}");
        var text = string.Join("; ", parts);
        return $"{c.PersonName}: {char.ToUpperInvariant(text[0])}{text[1..]}. It takes effect on their next page view.";
    }

    /// <summary>"IT and Manager", "IT, Manager and Admin".</summary>
    public static string Names(AppRoles roles)
    {
        var names = RoleColumns.Where(c => roles.HasFlag(c.Role)).Select(c => c.Label).ToList();
        return names.Count <= 1 ? string.Concat(names) : string.Join(", ", names[..^1]) + " and " + names[^1];
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = await service.PeopleAsync(new RoleFilter(RoleFilter, Site, Inactive), ct);
        Found = await service.SearchAsync(Q, ct);
        AllSites = await admin.ListSitesAsync(ct);
        Sites = AllSites.Where(s => s.IsActive).ToList();
        AnyAdmin = await service.AnyAdminLeftAsync(ct);
    }

    /// <summary>The site tick boxes of one row: active sites plus any inactive site the person already has.</summary>
    public IEnumerable<Site> SitesFor(PersonRolesRow row) =>
        AllSites.Where(s => s.IsActive || row.Grants.Any(g => g.SiteId == s.Id));
}
