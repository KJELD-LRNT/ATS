using AssetTracker.Core.Admin;
using AssetTracker.Core.Common;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

/// <summary>
/// Directory sync history (IT or admin, read-only for IT: sponsor decision D7) and the admin-only manual rerun
/// (runs synchronously in the request; fine at MVP scale). A rerun POST by anyone but an admin is 403.
/// </summary>
[Authorize(Policy = Policies.ItOrAdmin)]
public class SyncModel(AdminService admin, DirectorySyncService sync, ICurrentUser user) : PageModel
{
    /// <summary>Only admins see (and may use) "Run sync now".</summary>
    public bool CanRun => user.IsAdmin;

    public PagedResult<SyncRun> Runs { get; private set; } = new([], 1, 20, 0);
    public DirectorySyncStatus? Status { get; private set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    [TempData]
    public string? Message { get; set; }

    public Task OnGetAsync(CancellationToken ct) => LoadAsync(ct);

    public async Task<IActionResult> OnPostRunAsync(CancellationToken ct)
    {
        if (!user.IsAdmin) return Forbid();
        try
        {
            var run = await admin.RunSyncNowAsync(ct);
            Message = $"Sync run {run.Id} finished: {run.Status}.";
            return RedirectToPage();
        }
        catch (DomainException ex) { this.Show(ex); }
        await LoadAsync(ct);
        return Page();
    }

    public static IReadOnlyList<SyncIssue> Issues(SyncRun run) => DirectorySyncService.ParseIssues(run.IssuesJson);

    private async Task LoadAsync(CancellationToken ct)
    {
        Runs = await admin.ListSyncRunsAsync(PageNumber, ct: ct);
        Status = await sync.GetStatusAsync(ct);
    }
}
