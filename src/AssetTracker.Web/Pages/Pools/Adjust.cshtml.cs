using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AssetTracker.Web.Pages.Pools;

/// <summary>Stock ledger actions: receipt, loss, count adjustment and transfer. Each requires a reason.</summary>
[Authorize(Policy = Policies.ItOnly)]
public class AdjustModel(PoolService pools) : PageModel
{
    public Pool Pool { get; private set; } = null!;
    public List<SelectListItem> TransferTargets { get; private set; } = [];

    [TempData] public string? Message { get; set; }

    public Task OnGetAsync(int id, CancellationToken ct) => LoadAsync(id, ct);

    public Task<IActionResult> OnPostReceiptAsync(int id, int quantity, string? reason, CancellationToken ct) =>
        RunAsync(id, () => pools.ReceiveAsync(id, quantity, reason ?? "", ct), $"Received {quantity}.", ct);

    public Task<IActionResult> OnPostLossAsync(int id, int quantity, string? reason, CancellationToken ct) =>
        RunAsync(id, () => pools.RecordLossAsync(id, quantity, reason ?? "", ct), $"Recorded loss of {quantity}.", ct);

    public Task<IActionResult> OnPostCountAsync(int id, int counted, string? reason, CancellationToken ct) =>
        RunAsync(id, () => pools.AdjustCountAsync(id, counted, reason ?? "", ct), $"Available count set to {counted}.", ct);

    public Task<IActionResult> OnPostTransferAsync(int id, int toPoolId, int quantity, string? reason, CancellationToken ct) =>
        RunAsync(id, () => pools.TransferAsync(id, toPoolId, quantity, reason ?? "", ct), $"Transferred {quantity}.", ct);

    private async Task<IActionResult> RunAsync(int id, Func<Task> action, string success, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await action();
                Message = success;
                return RedirectToPage("Details", new { id });
            }
            catch (DomainException ex)
            {
                ModelState.AddModelError("", ex.Message);
            }
        }
        await LoadAsync(id, ct);
        return Page();
    }

    private async Task LoadAsync(int id, CancellationToken ct)
    {
        Pool = await pools.GetAsync(id, ct);
        TransferTargets = (await pools.GetTransferTargetsAsync(id, ct))
            .Select(p => new SelectListItem($"{p.Site?.Key} – {p.Sku} (available {p.Available})", p.Id.ToString()))
            .ToList();
    }
}
