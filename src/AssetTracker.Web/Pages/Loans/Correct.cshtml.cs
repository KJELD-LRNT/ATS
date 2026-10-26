using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Loans;

/// <summary>
/// Two-step correction: the form posts to <c>Preview</c> (effects and conflicts, nothing saved), which renders a
/// confirmation form posting to <c>Commit</c> with the required reason.
/// </summary>
[Authorize(Policy = Policies.ItOnly)]
public class CorrectModel(CorrectionService corrections, LoanHistoryService history, LendingService lending) : PageModel
{
    public class InputModel
    {
        public int HolderId { get; set; }

        /// <summary>Times are UTC (datetime-local inputs).</summary>
        public DateTime? StartedAt { get; set; }
        public DateOnly? DueDate { get; set; }
        public DateTime? ReturnedAt { get; set; }
        public bool Reopen { get; set; }
        public int? Quantity { get; set; }
        public int? ReturnedQuantity { get; set; }
        public string? Reason { get; set; }
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? Who { get; set; }

    public LoanHistory History { get; private set; } = null!;
    public IReadOnlyList<PersonOption> People { get; private set; } = [];
    public CorrectionPreview? Preview { get; private set; }

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(int id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        var l = History.Loan;
        Input = new InputModel
        {
            HolderId = l.HolderId, StartedAt = l.StartedAt.UtcDateTime, DueDate = l.DueDate,
            ReturnedAt = l.ClosedAt?.UtcDateTime, Quantity = l.Quantity, ReturnedQuantity = l.ReturnedQuantity,
        };
    }

    public async Task<IActionResult> OnPostPreviewAsync(int id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        try
        {
            Preview = await corrections.PreviewAsync(id, ToRequest(), ct);
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError("", ex.Message);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostCommitAsync(int id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        try
        {
            await corrections.CommitAsync(id, ToRequest(), ct);
            Message = "Correction recorded.";
            return RedirectToPage("Details", new { id });
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError("", ex.Message);
            try { Preview = await corrections.PreviewAsync(id, ToRequest(), ct); }
            catch (DomainException) { /* already reported */ }
        }
        return Page();
    }

    public string PersonName(string? id) =>
        int.TryParse(id, out var i)
            ? People.FirstOrDefault(p => p.Id == i)?.DisplayName ?? History.PersonName(i)
            : id ?? "";

    private async Task LoadAsync(int id, CancellationToken ct)
    {
        History = await history.GetAsync(id, ct);
        var people = (await lending.SearchPeopleAsync(Who, eligibleOnly: false, ct: ct)).ToList();
        // Keep a holder chosen in an earlier search selectable (and nameable) after Preview.
        if (Input.HolderId != 0 && Input.HolderId != History.Loan.HolderId && people.All(p => p.Id != Input.HolderId) &&
            await lending.GetPersonAsync(Input.HolderId, ct) is { } chosen)
            people.Insert(0, chosen);
        People = people;
    }

    /// <summary>Unchanged form values become "unchanged" (null) so second-level precision of inputs never creates a change.</summary>
    private CorrectionRequest ToRequest()
    {
        var l = History.Loan;
        var tagged = l.AssetId is not null;
        var started = Utc(Input.StartedAt);
        var returned = Utc(Input.ReturnedAt);
        return new CorrectionRequest
        {
            HolderId = Input.HolderId == 0 ? null : Input.HolderId,
            StartedAt = started is { } s && !SameSecond(s, l.StartedAt) ? s : null,
            DueDate = Input.DueDate,
            ClearDueDate = Input.DueDate is null && l.DueDate is not null,
            Reopen = tagged && Input.Reopen,
            ReturnedAt = (tagged && Input.Reopen) || returned is null || (l.ClosedAt is { } c && SameSecond(returned.Value, c))
                ? null : returned,
            Quantity = tagged ? null : Input.Quantity,
            ReturnedQuantity = tagged ? null : Input.ReturnedQuantity,
            Reason = Input.Reason,
        };
    }

    private static DateTimeOffset? Utc(DateTime? t) =>
        t is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Unspecified), TimeSpan.Zero) : null;

    private static bool SameSecond(DateTimeOffset a, DateTimeOffset b) =>
        a.UtcTicks / TimeSpan.TicksPerSecond == b.UtcTicks / TimeSpan.TicksPerSecond;

    public static string Show(FieldChange c, Func<string?, string> name) => c.Field switch
    {
        "holderId" => $"{name(c.From)} → {name(c.To)}",
        _ => $"{c.From ?? "–"} → {c.To ?? "–"}",
    };

    public static string Label(string field) => field switch
    {
        "holderId" => "Holder",
        "startedAt" => "Started (UTC)",
        "dueDate" => "Due date",
        "closedAt" => "Returned at (UTC)",
        "quantity" => "Quantity checked out",
        "returnedQuantity" => "Quantity returned",
        _ => field,
    };

    public static string? Local(DateTime? t) => t?.ToString("yyyy-MM-ddTHH:mm:ss");

    public bool IsTagged => History.Loan.AssetId is not null;
    public AssetState? AssetState => History.Loan.Asset?.State;
}
