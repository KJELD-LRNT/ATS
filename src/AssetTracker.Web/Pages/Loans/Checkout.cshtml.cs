using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Security;
using AssetTracker.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Web.Pages.Loans;

/// <summary>
/// Checkout: step 1 (GET) finds the item (tagged asset or quantity pool, one search box) and the person; step 2 (POST)
/// confirms. IT only — the people search is a global directory lookup managers must not have.
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

    /// <summary>The item search text: an exact tag, or anything the finder searches (model, serial number, phone number ...).</summary>
    [BindProperty(SupportsGet = true)] public string? Tag { get; set; }

    /// <summary>Item picked in the finder (they win over the typed text; the finder clears them when the text is edited).</summary>
    [BindProperty(SupportsGet = true)] public int? AssetId { get; set; }
    [BindProperty(SupportsGet = true)] public int? PoolId { get; set; }
    [BindProperty(SupportsGet = true)] public int Quantity { get; set; } = 1;

    /// <summary>Filters of the item finder, kept across "Find".</summary>
    [BindProperty(SupportsGet = true, Name = "type")] public int? FilterType { get; set; }
    [BindProperty(SupportsGet = true, Name = "site")] public int? FilterSite { get; set; }

    [BindProperty(SupportsGet = true)] public string? Who { get; set; }

    /// <summary>Person picked in the typeahead (step 2); the confirm form then offers just them, already selected.</summary>
    [BindProperty(SupportsGet = true)] public int? HolderId { get; set; }

    [BindProperty] public InputModel Input { get; set; } = new();

    public IndividualAsset? Asset { get; private set; }
    public Pool? Pool { get; private set; }
    public string? TargetError { get; private set; }

    /// <summary>Items matching the typed text when it is not an exact tag and more than one item matches (no-JavaScript finder).</summary>
    public IReadOnlyList<ItemOption> Matches { get; private set; } = [];
    public IReadOnlyList<PersonOption> People { get; private set; } = [];
    public List<SelectListItem> Types { get; private set; } = [];
    public List<SelectListItem> Sites { get; private set; } = [];
    public bool DirectoryStale { get; private set; }

    [TempData] public string? Message { get; set; }

    public const int MinSearchLength = 2;
    public const int ItemSuggestionCount = 10;

    public Task OnGetAsync(CancellationToken ct) => LoadAsync(ct);

    /// <summary>Suggestions for the "Who receives it?" typeahead: people who can receive a loan, with their site.</summary>
    public async Task<IActionResult> OnGetPeopleAsync(string? q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < MinSearchLength) return new JsonResult(Array.Empty<object>());
        var people = await lending.SearchPeopleAsync(q, eligibleOnly: true, take: 8, ct: ct);
        return new JsonResult(people.Select(p => new
        {
            id = p.Id, name = p.DisplayName, username = p.Username, email = p.Email, jobTitle = p.JobTitle,
            siteId = p.SiteId, siteKey = p.SiteKey, siteName = p.SiteName,
        }));
    }

    /// <summary>
    /// Suggestions for the "What is being lent?" finder: assets and pools matching the text and the type/site filters.
    /// An empty text lists what is available (browse). Items that cannot be lent are included with their state.
    /// </summary>
    public async Task<IActionResult> OnGetItemsAsync(string? q, int? type, int? site, CancellationToken ct)
    {
        var term = q?.Trim();
        // One typed character matches half the inventory: wait for two (an empty box is the browse view).
        if (!string.IsNullOrEmpty(term) && term.Length < MinSearchLength) return new JsonResult(Array.Empty<object>());
        var items = await lending.SearchItemsAsync(term, type, site, ItemSuggestionCount, ct);
        return new JsonResult(items.Select(i => new
        {
            kind = i.Kind, id = i.Id, key = i.Key, type = i.Type, typeId = i.TypeId, title = i.Title, description = i.Description,
            siteId = i.SiteId, siteKey = i.SiteKey, siteName = i.SiteName, canCheckOut = i.CanCheckOut, status = i.Status,
            holder = i.Holder, available = i.Available, loanId = i.LoanId,
        }));
    }

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

    private IQueryable<Pool> PoolQuery() => db.Pools.AsNoTracking().Include(p => p.Site).Include(p => p.Type);

    private async Task LoadAsync(CancellationToken ct)
    {
        DirectoryStale = await lending.IsDirectoryStaleAsync(ct);
        Types = await InventoryUi.TypeOptionsAsync(db, ct: ct);
        Sites = await InventoryUi.SiteOptionsAsync(db, ct: ct);
        await ResolveItemAsync(ct);

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

    /// <summary>
    /// The item to lend: the one picked in the finder (asset or pool id), else an exact tag, else whatever the typed text
    /// finds. Text that finds exactly one item selects it; text that finds several lists them in <see cref="Matches"/>.
    /// </summary>
    private async Task ResolveItemAsync(CancellationToken ct)
    {
        var text = Tag?.Trim() ?? "";
        var tag = Normalization.Code(text);
        if (AssetId is { } aid)
            Asset = await AssetQuery().FirstOrDefaultAsync(a => a.Id == aid, ct);
        else if (PoolId is { } pid)
            Pool = await PoolQuery().FirstOrDefaultAsync(p => p.Id == pid, ct);
        else if (tag.Length > 0)
        {
            Asset = await AssetQuery().FirstOrDefaultAsync(a => a.Tag == tag, ct);
            if (Asset is null)
            {
                var found = await lending.SearchItemsAsync(text, FilterType, FilterSite, ItemSuggestionCount, ct);
                if (found.Count == 1)
                {
                    if (found[0].Kind == "asset") Asset = await AssetQuery().FirstOrDefaultAsync(a => a.Id == found[0].Id, ct);
                    else Pool = await PoolQuery().FirstOrDefaultAsync(p => p.Id == found[0].Id, ct);
                }
                else if (found.Count > 1) Matches = found;
                else TargetError = $"No item found for “{text}”.";
            }
        }

        if (Asset is { State: not AssetState.Available })
            TargetError = $"Asset {Asset.Tag} is {Asset.State} and cannot be checked out.";
        else if (Pool is { Available: 0 }) TargetError = $"No units of {Pool.Sku} are available.";
        else if ((AssetId is not null || PoolId is not null) && Asset is null && Pool is null)
            TargetError = "That item no longer exists.";
    }
}
