using AssetTracker.Core.Notifications;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Ops;

/// <summary>IT view of the mail outbox: queue health, failed/skipped rows and manual requeue.</summary>
[Authorize(Policy = Policies.ItOnly)]
public class MailModel(OutboxMonitor monitor) : PageModel
{
    public const int PageSize = 50;

    public OutboxStatus Status { get; private set; } = null!;
    public OutboxPage Rows { get; private set; } = null!;

    [TempData]
    public string? Message { get; set; }

    public async Task OnGetAsync(int p = 1, CancellationToken ct = default)
    {
        Status = await monitor.GetStatusAsync(ct);
        Rows = await monitor.ListAsync(page: p, pageSize: PageSize, ct: ct);
    }

    public async Task<IActionResult> OnPostRequeueAsync(long id, CancellationToken ct)
    {
        await monitor.RequeueAsync(id, ct);
        Message = $"Notification {id} requeued.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRequeueAllAsync(CancellationToken ct)
    {
        var count = await monitor.RequeueAllFailedAsync(ct);
        Message = $"{count} failed notification(s) requeued.";
        return RedirectToPage();
    }
}
