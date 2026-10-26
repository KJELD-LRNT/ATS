using AssetTracker.Core.Lending;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Loans;

/// <summary>Effective loan state and its full event trail (originals and the corrections that supersede them).</summary>
[Authorize(Policy = Policies.InventoryRead)]
public class DetailsModel(LoanHistoryService history, ICurrentUser user) : PageModel
{
    public LoanHistory History { get; private set; } = null!;
    public bool CanEdit => user.IsIt;

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(int id, CancellationToken ct) => History = await history.GetAsync(id, ct);

    public string Describe(FieldChange c) => c.Field switch
    {
        "holderId" => $"Holder: {Name(c.From)} → {Name(c.To)}",
        "startedAt" => $"Started: {c.From} → {c.To}",
        "dueDate" => $"Due date: {c.From ?? "none"} → {c.To ?? "none"}",
        "closedAt" => $"Returned at: {c.From ?? "open"} → {c.To ?? "open"}",
        "quantity" => $"Quantity: {c.From} → {c.To}",
        "returnedQuantity" => $"Returned quantity: {c.From} → {c.To}",
        _ => $"{c.Field}: {c.From} → {c.To}",
    };

    /// <summary>One side of a change for display (holder ids become names; empty dates read "none"/"open").</summary>
    public string Value(FieldChange c, string? v) => c.Field switch
    {
        "holderId" => Name(v),
        "dueDate" => v ?? "none",
        "closedAt" => v ?? "open",
        _ => v ?? "–",
    };

    private string Name(string? id) => int.TryParse(id, out var i) ? History.PersonName(i) : id ?? "";
}
