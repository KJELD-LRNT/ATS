using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Web.Pages.Loans;

/// <summary>
/// Checkout: step 1 (GET) picks the item (asset tag or pool) and searches people; step 2 (POST) confirms.
/// IT only — the people search is a global directory lookup managers must not have.
/// </summary>
[Authorize(Policy = Policies.ItOnly)]
public class CheckoutModel(LendingService lending, AppDbContext db) : PageModel
{
    public class InputModel
    {
        public int? AssetId { get; set; }
        public int? PoolId { get; set; }
        public int Quantity { get; set; } = 1;
        public int HolderId { get; set; }
        public DateOnly? DueDate { get; set; }
        public string? Condition { get; set; }
        public string? Note { get; set; }
    }

    [BindProperty(SupportsGet = true)] public string? Tag { get; set; }
    [BindProperty(SupportsGet = true)] public int? AssetId { get; set; }
    [BindProperty(SupportsGet = true)] public int? PoolId { get; set; }
    [BindProperty(SupportsGet = true)] public int Quantity { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public string? Who { get; set; }

    [BindProperty] public InputModel Input { get; set; } = new();

    public IndividualAsset? Asset { get; private set; }
    public Pool? Pool { get; private set; }
    public string? TargetError { get; private set; }
    public IReadOnlyList<PersonOption> People { get; private set; } = [];
    public List<SelectListItem> Pools { get; private set; } = [];
    public bool DirectoryStale { get; private set; }

    [TempData] public string? Message { get; set; }

    public Task OnGetAsync(CancellationToken ct) => LoadAsync(ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            var result = await lending.CheckoutAsync(new CheckoutRequest(Input.HolderId, Input.AssetId, Input.PoolId,
                Input.AssetId is not null ? 1 : Input.Quantity, Input.DueDate, Input.Condition, Input.Note), ct);
            Message = $"Checked out (quantity {result.Loan.Quantity})." +
                      (result.DirectoryStale ? " Warning: the people directory snapshot is stale; this was recorded." : "");
            return RedirectToPage("Details", new { id = result.Loan.Id });
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError("", ex.Message);
        }
        AssetId = Input.AssetId;
        PoolId = Input.PoolId;
        Quantity = Input.Quantity;
        await LoadAsync(ct);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        DirectoryStale = await lending.IsDirectoryStaleAsync(ct);
        Pools = (await db.Pools.AsNoTracking().Include(p => p.Site).Where(p => p.Available > 0)
                .OrderBy(p => p.Site!.Key).ThenBy(p => p.Sku).ToListAsync(ct))
            .Select(p => new SelectListItem($"{p.Site?.Key} – {p.Sku} ({p.Available} available)", p.Id.ToString(),
                p.Id == PoolId)).ToList();

        var tag = Normalization.Code(Tag);
        if (AssetId is { } aid)
            Asset = await db.Assets.AsNoTracking().Include(a => a.Site).FirstOrDefaultAsync(a => a.Id == aid, ct);
        else if (tag.Length > 0)
            Asset = await db.Assets.AsNoTracking().Include(a => a.Site).FirstOrDefaultAsync(a => a.Tag == tag, ct);
        else if (PoolId is { } pid)
            Pool = await db.Pools.AsNoTracking().Include(p => p.Site).FirstOrDefaultAsync(p => p.Id == pid, ct);

        if (tag.Length > 0 && Asset is null) TargetError = $"No asset with tag {tag}.";
        else if (Asset is { State: not AssetState.Available })
            TargetError = $"Asset {Asset.Tag} is {Asset.State} and cannot be checked out.";
        else if (Pool is { Available: 0 }) TargetError = $"No units of {Pool.Sku} are available.";

        if (Quantity < 1) Quantity = 1;
        People = await lending.SearchPeopleAsync(Who, eligibleOnly: true, ct: ct);
    }
}
