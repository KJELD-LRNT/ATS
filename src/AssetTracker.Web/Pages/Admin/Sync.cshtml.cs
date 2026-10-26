using AssetTracker.Core.Admin;
using AssetTracker.Core.Common;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

/// <summary>Directory sync history and manual rerun (runs synchronously in the request; fine at MVP scale).</summary>
[Authorize(Policy = Policies.AdminOnly)]
public class SyncModel(AdminService admin, DirectorySyncService sync) : PageModel
{
    public PagedResult<SyncRun> Runs { get; private set; } = new([], 1, 20, 0);
    public DirectorySyncStatus? Status { get; private set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    [TempData]
    public string? Message { get; set; }

    public Task OnGetAsync(CancellationToken ct) => LoadAsync(ct);

    public async Task<IActionResult> OnPostRunAsync(CancellationToken ct)
    {
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
