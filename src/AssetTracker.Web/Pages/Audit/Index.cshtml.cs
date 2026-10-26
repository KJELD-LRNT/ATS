using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Audit;

/// <summary>Read-only, paged audit log. Visibility: IT only (to be confirmed with the client).</summary>
[Authorize(Policy = Policies.ItOnly)]
public class IndexModel(AuditQueryService audit) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Action { get; set; }
    [BindProperty(SupportsGet = true)] public string? Entity { get; set; }
    [BindProperty(SupportsGet = true)] public string? EntityId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Actor { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public PagedResult<AuditEvent> Result { get; private set; } = new([], 1, 50, 0);
    public IReadOnlyList<string> EntityTypes { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        EntityTypes = await audit.EntityTypesAsync(ct);
        Result = await audit.SearchAsync(new AuditFilter(Action, Entity, EntityId, Actor, From, To), PageNumber, ct: ct);
    }

    public Dictionary<string, string> RouteFor(int page)
    {
        var all = new Dictionary<string, string?>
        {
            ["action"] = Action, ["entity"] = Entity, ["entityId"] = EntityId, ["actor"] = Actor,
            ["from"] = From?.ToString("yyyy-MM-dd"), ["to"] = To?.ToString("yyyy-MM-dd"), ["p"] = page.ToString(),
        };
        return all.Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value!);
    }
}
