using AssetTracker.Core.Admin;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

/// <summary>
/// Manager-to-site grants. A grant only takes effect while the person is in the manager AD group.
/// The people search here is admin-only; managers never get a people search.
/// </summary>
[Authorize(Policy = Policies.AdminOnly)]
public class GrantsModel(AdminService admin) : PageModel
{
    public IReadOnlyList<GrantRow> Grants { get; private set; } = [];
    public IReadOnlyList<Site> Sites { get; private set; } = [];
    public IReadOnlyList<PersonPick> People { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [TempData]
    public string? Message { get; set; }

    public Task OnGetAsync(CancellationToken ct) => LoadAsync(ct);

    public async Task<IActionResult> OnPostAddAsync(int personId, int siteId, CancellationToken ct)
    {
        try
        {
            await admin.AddGrantAsync(personId, siteId, ct);
            Message = "Grant added.";
            return RedirectToPage(new { q = Q });
        }
        catch (DomainException ex) { this.Show(ex); }
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(int grantId, CancellationToken ct)
    {
        try
        {
            await admin.RemoveGrantAsync(grantId, ct);
            Message = "Grant removed.";
            return RedirectToPage();
        }
        catch (DomainException ex) { this.Show(ex); }
        await LoadAsync(ct);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Grants = await admin.ListGrantsAsync(ct);
        Sites = (await admin.ListSitesAsync(ct)).Where(s => s.IsActive).ToList();
        People = await admin.SearchPeopleAsync(Q, ct);
    }
}
