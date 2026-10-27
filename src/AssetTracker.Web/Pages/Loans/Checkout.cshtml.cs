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

    /// <summary>Person picked in the typeahead (step 2); the confirm form then offers just them, already selected.</summary>
    [BindProperty(SupportsGet = true)] public int? HolderId { get; set; }

    [BindProperty] public InputModel Input { get; set; } = new();

    public IndividualAsset? Asset { get; private set; }
    public Pool? Pool { get; private set; }
    public string? TargetError { get; private set; }
    public IReadOnlyList<PersonOption> People { get; private set; } = [];
    public List<SelectListItem> Pools { get; private set; } = [];
    public bool DirectoryStale { get; private set; }

    [TempData] public string? Message { get; set; }

    public Task OnGetAsync(CancellationToken ct) => LoadAsync(ct);

    /// <summary>Suggestions for the "Who receives it?" typeahead: people who can receive a loan, with their site.</summary>
    public async Task<IActionResult> OnGetPeopleAsync(string? q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < MinSearchLength) return new JsonResult(Array.Empty<object>());
        var people = await lending.SearchPeopleAsync(q, eligibleOnly: true, take: 8, ct: ct);
        return new JsonResult(people.Select(p => new
        {
            id = p.Id, name = p.DisplayName, username = p.Username, email = p.Email, jobTitle = p.JobTitle,
            siteKey = p.SiteKey, siteName = p.SiteName,
        }));
    }

    public const int MinSearchLength = 2;

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
        HolderId = Input.HolderId;
        await LoadAsync(ct);
        return Page();
    }

    private IQueryable<IndividualAsset> AssetQuery() => db.Assets.AsNoTracking().Include(a => a.Site).Include(a => a.Type)
        .Include(a => a.Model).ThenInclude(m => m!.Vendor);

    private async Task LoadAsync(CancellationToken ct)
    {
        DirectoryStale = await lending.IsDirectoryStaleAsync(ct);
        Pools = (await db.Pools.AsNoTracking().Include(p => p.Site).Where(p => p.Available > 0)
                .OrderBy(p => p.Site!.Key).ThenBy(p => p.Sku).ToListAsync(ct))
            .Select(p => new SelectListItem($"{p.Site?.Key} – {p.Sku} ({p.Available} available)", p.Id.ToString(),
                p.Id == PoolId)).ToList();

        var tag = Normalization.Code(Tag);
        if (AssetId is { } aid)
            Asset = await AssetQuery().FirstOrDefaultAsync(a => a.Id == aid, ct);
        else if (tag.Length > 0)
            Asset = await AssetQuery().FirstOrDefaultAsync(a => a.Tag == tag, ct);
        else if (PoolId is { } pid)
            Pool = await db.Pools.AsNoTracking().Include(p => p.Site).Include(p => p.Type)
                .FirstOrDefaultAsync(p => p.Id == pid, ct);

        if (tag.Length > 0 && Asset is null) TargetError = $"No asset with tag {tag}.";
        else if (Asset is { State: not AssetState.Available })
            TargetError = $"Asset {Asset.Tag} is {Asset.State} and cannot be checked out.";
        else if (Pool is { Available: 0 }) TargetError = $"No units of {Pool.Sku} are available.";

        if (Quantity < 1) Quantity = 1;

        // A person picked from the typeahead wins over the typed text; if they cannot receive a loan after all
        // (deactivated meanwhile), fall back to searching the text.
        var picked = HolderId is { } hid ? await lending.GetPersonAsync(hid, ct) : null;
        if (picked is { CanReceiveNewLoan: true })
        {
            People = [picked];
            Input.HolderId = picked.Id;
        }
        else
        {
            People = await lending.SearchPeopleAsync(Who, eligibleOnly: true, ct: ct);
        }
    }
}
