using AssetTracker.Core.Admin;
using AssetTracker.Core.Common;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

/// <summary>
/// Roles that are assigned inside the application instead of through AD groups (Facility). Same people search as the
/// manager grants; admin only.
/// </summary>
[Authorize(Policy = Policies.AdminOnly)]
public class SystemRolesModel(RoleAssignmentService roles, AdminService admin) : PageModel
{
    public IReadOnlyList<RoleAssignmentRow> Assignments { get; private set; } = [];
    public IReadOnlyList<PersonPick> People { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    [TempData]
    public string? Message { get; set; }

    public Task OnGetAsync(CancellationToken ct) => LoadAsync(ct);

    public async Task<IActionResult> OnPostAddAsync(int personId, CancellationToken ct)
    {
        try
        {
            await roles.AssignAsync(personId, AppRoles.Facility, ct);
            Message = "Facility role assigned.";
            return RedirectToPage(new { q = Q });
        }
        catch (DomainException ex) { this.Show(ex); }
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id, CancellationToken ct)
    {
        try
        {
            await roles.RemoveAsync(id, ct);
            Message = "Role removed.";
            return RedirectToPage();
        }
        catch (DomainException ex) { this.Show(ex); }
        await LoadAsync(ct);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Assignments = await roles.ListAsync(ct);
        People = await admin.SearchPeopleAsync(Q, ct);
    }
}
